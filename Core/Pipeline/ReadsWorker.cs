using System.Diagnostics;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed class ReadsWorker
{
    private sealed record PendingTick(long Tick, double PostedMs, double ExpiresMs);

    private sealed record KeptRead(OpenCvSharp.Mat Image, string Text, string? Name, OpenCvSharp.Rect Region, int WindowWidth, int WindowHeight, bool OffCentre);

    private PipelineSettings _settings;
    private readonly PlateNameJudge _judge;
    private readonly ReadPolicy _policy;
    private readonly WhiteDuplicateRule _duplicates;
    private readonly SessionExclusions _session;
    private readonly SessionNames _names;
    private readonly PipelineStats? _stats;
    private readonly ILog _log;
    private readonly IClock _wall;
    private readonly IUpdateSink _sink;
    private readonly IMonotonicClock _clock;
    private readonly Dictionary<int, PlateState> _states = new();
    private readonly Dictionary<int, List<KeptRead>> _kept = new();
    private readonly HashSet<int> _offCentreStops = new();
    private int _unknownCropsHanded;
    private readonly HashSet<string> _confirmedLogged = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SessionMemoryEntry> _memory = Array.Empty<SessionMemoryEntry>();
    private PendingTick? _pending;
    private long _tick;
    private int _epoch = int.MinValue;

    public ReadsWorker(
        PipelineSettings settings, IPlateImageOps ops, PlateNameJudge judge, SessionExclusions session, IUpdateSink sink, IMonotonicClock clock,
        SessionNames? names = null, PipelineStats? stats = null, ILog? log = null, IClock? wallClock = null, INameResolver? resolver = null)
    {
        _settings = settings;
        Ops = ops;
        _judge = judge;
        _session = session;
        _sink = sink;
        _clock = clock;
        _names = names ?? new SessionNames();
        _stats = stats;
        _log = log ?? NullLog.Instance;
        _wall = wallClock ?? SystemClock.Instance;
        _duplicates = new WhiteDuplicateRule(settings.DuplicateWindowTicks);
        _policy = new ReadPolicy(settings, resolver ?? new EvidenceNameResolver(), judge, _duplicates, _names);
    }

    public void UseRates(PipelineSettings settings)
    {
        _settings = settings;
        _duplicates.WindowTicks = settings.DuplicateWindowTicks;
        _policy.UseRates(settings);
    }

    public IPlateImageOps Ops { get; set; }

    public ITraceSink? Trace { get; set; }

    public IReadOnlyDictionary<int, PlateState> States => _states;

    public Action<OpenCvSharp.Mat, string>? KeepCrop { get; set; }

    public bool HasPending => _pending is not null;

    public bool Accepts(CapturedFrame frame)
    {
        if (_pending is not { } pending)
            return false;
        foreach (var region in frame.Regions)
            if (region.Requester == PipelineGroup.Reads && region.Tag == pending.Tick && region.OneTime)
                return true;
        return false;
    }

    public bool Prepare(IndexSnapshot snapshot, PipelineEnvironment env, double nowMs, SocialConfig config)
    {
        var workStartMs = _clock.NowMs;
        SyncEpoch(snapshot);
        _judge.Config = config;
        _judge.NowMs = nowMs;
        ForgetEnded(snapshot);
        if (_pending is { } waiting)
        {
            _stats?.AddSkipped(PipelineGroup.Reads);
            if (nowMs - waiting.PostedMs < _settings.ReadPendingMaxMs && !(snapshot.NewFrameMs > waiting.ExpiresMs))
            {
                if (Trace.Line(_clock.NowMs, "rd", "wait") is { } wait)
                    Trace!.Write(wait.Int("tick", waiting.Tick).Num("ageMs", nowMs - waiting.PostedMs, 1).Text());
                return false;
            }
        }

        _tick++;
        _stats?.AddPlateTick();

        _memory = snapshot.SessionMemory;
        var epoch = snapshot.Epoch;
        var stamp = Stamp(snapshot, nowMs);
        var updates = new List<IndexUpdate>();
        ReturnNotFoundToReading(snapshot, stamp, epoch, updates);
        var width = env.WindowWidth;
        var height = env.WindowHeight;
        var scale = height / 1440.0;
        var (centreX, centreY) = RegionPlanner.ScanCentre(env.Geometry, env.Boundary, _settings);
        var list = new List<ReadCandidate>();
        var confirmed = new List<(int Id, bool Urgent, double LastReadMs)>();
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Identity is { Rsi: RsiState.Verified })
                DropKept(marker.Id);
            if (marker.Status == PlateStatus.Confirmed && _states.TryGetValue(marker.Id, out var held))
            {
                held.InContact = IsVerified(marker) && InContact(marker, snapshot.Markers);
                if (held.InContact)
                    held.Reserve = 0;
                if (ConfirmedReadDue(held, marker, nowMs) is { } due)
                    confirmed.Add((marker.Id, due, held.LastReadMs));
            }
            if (marker.Status is not (PlateStatus.Unconfirmed or PlateStatus.Unclear))
                continue;
            var dx = marker.X + marker.Width / 2.0 - centreX;
            var dy = marker.Y - centreY;
            var last = _states.TryGetValue(marker.Id, out var state) ? state.LastReadMs : double.NegativeInfinity;
            list.Add(new ReadCandidate(marker.Id, marker.Status == PlateStatus.Unclear, Math.Sqrt(dx * dx + dy * dy), last));
        }

        var selected = ReadScheduler.Select(list, _settings.MaxReadsPerTick).ToList();
        var rereads = confirmed.OrderByDescending(c => c.Urgent).ThenBy(c => c.LastReadMs).ThenBy(c => c.Id)
            .Take(Math.Max(0, _settings.MaxReadsPerTick - selected.Count)).Select(c => c.Id).ToHashSet();
        selected.AddRange(rereads);
        var madeFrom = snapshot.NewFrameMs;
        var target = double.IsFinite(madeFrom) ? madeFrom + snapshot.NominalFrameIntervalMs : nowMs;
        var readExpiryMs = RegionPlanner.OneTimeExpiryMs(_settings.ReadRequestMs, snapshot.NominalFrameIntervalMs, _settings);
        foreach (var id in selected)
        {
            if (snapshot.Find(id) is not { } marker)
                continue;
            if (_states.TryGetValue(id, out var known) && known.Status is PlateStatus.Confirmed or PlateStatus.Stopped && !rereads.Contains(id))
                continue;
            if (RegionPlanner.Crop(marker, target, _settings.ReadMarginPx * scale, width, height, _settings) is not { } crop)
                continue;
            var spec = new RequestSpec(PipelineGroup.Reads, RequestKind.ReadCrop, RegionPlanner.ToPixelRect(crop), _tick, madeFrom, nowMs, nowMs + readExpiryMs, true);
            updates.Add(new RequestUpdate(PipelineGroup.Reads, stamp, epoch, id, spec));
        }

        _pending = updates.Any(u => u is RequestUpdate) ? new PendingTick(_tick, nowMs, nowMs + readExpiryMs) : null;
        if (Trace.Line(_clock.NowMs, "rd", "prep") is { } prep)
        {
            prep.Int("tick", _tick).Int("candidates", list.Count).Num("madeFrom", madeFrom).Array("asked");
            foreach (var update in updates)
                if (update is RequestUpdate asked)
                    prep.Item().Int("mid", asked.MarkerId).Box("crop", asked.Spec.Region);
            Trace!.Write(prep.EndArray().Text());
        }
        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);
        return true;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060", Justification = "same Process signature as the other workers")]
    public void Process(IndexSnapshot snapshot, CapturedFrame frame, PipelineEnvironment env, SocialConfig config)
    {
        var workStartMs = _clock.NowMs;
        SyncEpoch(snapshot);
        if (_pending is not { } pending)
            return;
        _pending = null;
        ForgetEnded(snapshot);
        if (Ops.Layers is { } layers)
        {
            layers.Rule = AberrationWatch.RuleFor(snapshot.Aberration, measures: false);
            layers.TakeVerdicts();
        }

        _judge.Config = config;
        _judge.NowMs = frame.FrameMs;
        _memory = snapshot.SessionMemory;

        var updates = new List<IndexUpdate>();
        var freshnessMs = IndexModel.FreshnessLimitMs(snapshot, _settings);
        var readStart = Stopwatch.GetTimestamp();
        var reads = 0;
        foreach (var region in frame.Regions)
        {
            if (region.Requester != PipelineGroup.Reads || region.Kind != RequestKind.ReadCrop || region.Tag != pending.Tick || !region.OneTime || region.Image.Empty())
                continue;
            if (snapshot.Find(region.MarkerId) is not { } marker)
                continue;
            var (px, py, _, _) = marker.Predict(frame.FrameMs, _settings);
            var item = new ReadItem(marker.Id, new CropImage(region.Image, region.Region, false), new PixelRect(px, py, marker.Width, marker.Height), marker.Height);
            ReadOne(frame, pending, item, marker, freshnessMs, updates);
            reads++;
        }

        if (reads > 0)
            _stats?.AddReadTick(reads, _settings.MaxReadsPerTick, Elapsed(readStart));

        UpdateTiming.Send(_sink, updates, _clock, workStartMs, snapshot.NewFrameMs);
    }

    private void ReadOne(
        CapturedFrame frame, PendingTick pending, ReadItem item, MarkerRecord marker, double freshnessMs, List<IndexUpdate> updates)
    {
        var frameMs = frame.FrameMs;
        if (!_states.TryGetValue(item.Id, out var state))
            _states[item.Id] = state = new PlateState(item.Id, frameMs);
        if (state.Status == PlateStatus.Confirmed && marker.Status == PlateStatus.Confirmed)
        {
            ReadConfirmed(state, item, marker, frameMs, freshnessMs, updates);
            return;
        }

        if (state.Status is PlateStatus.Confirmed or PlateStatus.Stopped)
            return;

        var result = Ops.Read(item);
        KeepRead(frame, item, result);
        var background = _stats is not null || Trace is { Enabled: true }
            ? PlateBackground.Measure(item.Crop.Image, marker.Line.X - item.Crop.Origin.X, marker.Line.Y - item.Crop.Origin.Y, marker.Line.Width, marker.Line.Height)
            : null;
        if (background is { } bg)
            _stats?.AddBandRead(PlateBackground.BandOf(bg), result.Class == ObservationClass.Name);
        var nameBox = result.Rows is { Count: > 0 } rows ? rows[^1] : (PixelRect?)null;
        var observation = new ReadObservation(
            frameMs, frame.Sequence, result.Class, result.Name, result.O0 ?? Array.Empty<Detection.O0Glyph>(),
            nameBox?.Width ?? 0.0, result.Color, nameBox, result.Reason, item.LineHeight);
        var outcome = _policy.Apply(state, observation, pending.Tick, _memory);
        if (Trace.Line(_clock.NowMs, "rd", "read") is { } traced)
        {
            traced.Int("seq", frame.Sequence).Num("fms", frameMs).Int("tick", pending.Tick).Int("mid", item.Id).Str("st", marker.Status.ToString())
                .Box("line", marker.Line).Str("class", result.Class.ToString()).Str("name", result.Name).Str("reason", result.Reason)
                .Str("colour", result.Color?.ToString()).Int("rows", result.Rows?.Count ?? 0).Str("policy", outcome.Event.ToString()).Str("kind", outcome.Kind.ToString()).Str("decided", outcome.Name)
                .Bool("restored", outcome.Restored).Str("stateNow", state.Status.ToString()).Num("bg", background ?? double.NaN, 0);
            if (result.Anchor is { } anchor)
                traced.Box("anchor", anchor);
            if (nameBox is { } box)
                traced.Box("nameBox", box);
            if (outcome.DuplicateOf is { } other)
                traced.Int("duplicateOf", other);
            Trace!.Write(traced.Text());
        }

        if (outcome.DuplicateOf is not null && outcome.Name is { } duplicate)
            ExcludeDuplicate(duplicate, outcome.DuplicateOf.Value, frameMs, updates);

        if (outcome.Event == PolicyEvent.Stopped && result.Reason == ReadResult.OffCentre)
            _offCentreStops.Add(state.Id);
        else if (outcome.Event == PolicyEvent.Stopped && state.IgnoredReads < _settings.IgnoredReadsLimit)
            DropKept(state.Id);

        if (outcome.Event == PolicyEvent.Stopped)
        {
            updates.Add(new StatusUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, PlateStatus.Stopped));
            _stats?.AddStopped();
        }
        else if (outcome.Event == PolicyEvent.Confirmed && result.Anchor is { } anchor && result.Rows is { Count: > 0 } nameRows)
        {
            if (outcome.Restored)
            {
                _log.Write(_wall.UtcNow, $"Restored: {outcome.Name}");
                _stats?.AddRestored();
            }
            else if (_confirmedLogged.Add(outcome.Name!))
                _log.Write(_wall.UtcNow, $"Confirmed: {outcome.Name}");

            var offset = Confirm(state, outcome.Name!, item, anchor, nameRows, result.NameLine, frameMs, updates);
            state.RunName = null;
            state.RunVotes = 0;
            state.Reserve = 0;
            _stats?.AddConfirmed();
            if (outcome.Restored && _settings.RestoredRemeasureMaxReads > 0)
            {
                state.RemeasureLeft = _settings.RestoredRemeasureMaxReads;
                state.MeasuredNameRel = offset;
                state.MeasuredAgreeing = 1;
            }

            if (outcome.Restored)
            {
                state.Disputed = true;
                state.LastVoteMs = frameMs;
            }
        }
        else if (state.Status is PlateStatus.Unconfirmed or PlateStatus.Unclear)
        {
            if (marker.Status != state.Status)
                updates.Add(new StatusUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, state.Status));
            if (result.Anchor is { } found && marker.Status != PlateStatus.Confirmed && frameMs - marker.LastFoundMs > freshnessMs)
                updates.Add(new PositionUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, found));
        }

        if (result.Color is { } colour && colour != marker.PlateColour)
            updates.Add(new PlateColourUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, colour));
    }

    private PixelRect Confirm(
        PlateState state, string name, ReadItem item, PixelRect anchor, IReadOnlyList<PixelRect> rows, NameLineInfo? nameLine, double frameMs, List<IndexUpdate> updates)
    {
        var (line, offset, rel, refined) = MeasureOffsets(item, anchor, rows);
        updates.Add(new PositionUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, line));
        updates.Add(new ConfirmUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, name, offset, rel, nameLine));
        if (Trace.Line(_clock.NowMs, "rd", "confirm") is { } traced)
            Trace!.Write(traced.Num("fms", frameMs).Int("mid", state.Id).Str("name", name).Box("line", line).Box("nameRel", offset).Box("plateRel", rel)
                .Bool("subPixel", refined).Num("nameW", nameLine?.Width ?? double.NaN).Num("nameGap", nameLine?.Gap ?? double.NaN).Text());
        return offset;
    }

    private static (PixelRect Line, PixelRect NameRel, PixelRect PlateRel, bool Refined) MeasureOffsets(ReadItem item, PixelRect anchor, IReadOnlyList<PixelRect> rows)
    {
        var plate = Union(rows.Append(anchor));
        var nameBox = ClipToCrop(Union(rows), item.Crop);
        var origin = item.Crop.Origin;
        var local = new OpenCvSharp.Rect((int)Math.Round(anchor.X - origin.X), (int)Math.Round(anchor.Y - origin.Y), (int)Math.Round(anchor.Width), (int)Math.Round(anchor.Height));
        var fix = LineSubPixel.Measure(item.Crop.Image, local);
        var centreX = fix.Refined ? fix.CentreX + origin.X : anchor.X + anchor.Width / 2.0;
        var top = fix.Refined ? fix.Top + origin.Y : anchor.Y;
        var line = new PixelRect(centreX - anchor.Width / 2.0, top, anchor.Width, anchor.Height);
        var offset = new PixelRect(nameBox.X - centreX, nameBox.Y - top, nameBox.Width, nameBox.Height);
        var rel = new PixelRect(plate.X - centreX, plate.Y - top, plate.Width, plate.Height);
        return (line, offset, rel, fix.Refined);
    }

    private static bool Remeasures(PlateState state) => state.Status == PlateStatus.Confirmed && state.RemeasureLeft > 0;

    private bool? ConfirmedReadDue(PlateState state, MarkerRecord marker, double nowMs)
    {
        if (state.Status != PlateStatus.Confirmed)
            return null;
        if (Remeasures(state))
            return true;
        if (!IsVerified(marker))
            return null;
        var hz = state.Disputed ? _settings.DisputedReadHz : _settings.ConfirmedReadHz;
        return nowMs - state.LastReadMs >= 1000.0 / hz - 500.0 / _settings.PlateHz ? state.Disputed : null;
    }

    private static bool IsVerified(MarkerRecord marker) =>
        marker.Status == PlateStatus.Confirmed && marker.Name is { } name && marker.Identity is { Rsi: RsiState.Verified } identity
        && string.Equals(identity.Handle, name, StringComparison.OrdinalIgnoreCase);

    private void ReadConfirmed(PlateState state, ReadItem item, MarkerRecord marker, double frameMs, double freshnessMs, List<IndexUpdate> updates)
    {
        state.LastReadMs = frameMs;
        var result = Ops.Read(item);
        if (Remeasures(state))
            Remeasure(state, item, marker, result, frameMs, updates);
        if (IsVerified(marker))
            Weigh(state, marker, result, frameMs, freshnessMs, updates);
    }

    private void Weigh(PlateState state, MarkerRecord marker, ReadResult result, double frameMs, double freshnessMs, List<IndexUpdate> updates)
    {
        _stats?.AddIdentityRead();
        var blob = HasBlob(marker, result);
        var strong = blob ? 0 : StrongLetters(marker, result);
        var strongDisproof = strong >= _settings.StrongDisproofLetters;
        var (vote, excluded) = blob || strongDisproof ? (null, false) : Vote(marker, result, frameMs, freshnessMs);
        var support = !blob && !strongDisproof && vote is null && Supports(marker, result, frameMs, freshnessMs);
        var opened = false;
        var ended = false;
        string? disproved = null;
        var disprovedExcluded = false;
        if (support)
        {
            state.RunName = null;
            state.RunVotes = 0;
            if (!state.InContact)
                state.Reserve = Math.Min(_settings.BeliefReserveMax, state.Reserve + 1);
        }

        if (vote is not null)
        {
            if (!state.Disputed)
            {
                state.Disputed = opened = true;
                _stats?.AddDispute();
            }

            state.LastVoteMs = frameMs;
            if (state.RunName == vote && state.RunExcluded == excluded)
                state.RunVotes++;
            else
            {
                state.RunName = vote;
                state.RunExcluded = excluded;
                state.RunVotes = 1;
            }

            if (state.RunVotes >= _settings.DisproofVotes + state.Reserve)
                (disproved, disprovedExcluded) = (vote, excluded);
        }
        else if (strongDisproof)
            disproved = LookAlikes.FoldIdentity(result.Name!);
        else if (state.Disputed && frameMs - state.LastVoteMs > _settings.DisputeEndMs)
        {
            state.Disputed = false;
            state.RunName = null;
            state.RunVotes = 0;
            ended = true;
        }

        if (Trace.Line(_clock.NowMs, "rd", "belief") is { } traced)
            Trace!.Write(traced.Num("fms", frameMs).Int("mid", state.Id).Str("confirmed", marker.Name).Str("class", result.Class.ToString()).Str("name", result.Name)
                .Num("margin", result.MinLetterMargin, 2).Str("hitEv", marker.HitNameEvidence.ToString()).Bool("blob", blob).Str("vote", vote).Bool("excluded", excluded)
                .Bool("support", support).Bool("opened", opened).Bool("disputed", state.Disputed).Bool("ended", ended).Int("run", state.RunVotes)
                .Int("reserve", state.Reserve).Bool("contact", state.InContact).Int("strong", strong).Str("margins", MarginsText(result.LetterMargins))
                .Str("disproved", disproved).Text());

        if (disproved is null)
            return;
        if (disprovedExcluded)
        {
            _log.Write(_wall.UtcNow, $"Identity: {marker.Name} excluded, read as {result.Name} on its plate");
            state.Status = PlateStatus.Stopped;
            state.Disputed = false;
            updates.Add(new StatusUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, PlateStatus.Stopped));
            _stats?.AddIdentityExcluded();
            _stats?.AddStopped();
            return;
        }

        _log.Write(_wall.UtcNow, strongDisproof
            ? $"Identity: {marker.Name} disproved by one strong read ({strong} strong letters), read as {result.Name} on its plate"
            : $"Identity: {marker.Name} disproved, read as {result.Name} on its plate");
        _states[state.Id] = new PlateState(state.Id, state.BornMs)
        {
            EverNamed = true,
            ReadsTotal = state.ReadsTotal,
            IgnoredReads = state.IgnoredReads,
            LastReadMs = frameMs,
        };
        updates.Add(new UnconfirmUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id));
        _stats?.AddIdentityDisproved();
    }

    private int StrongLetters(MarkerRecord marker, ReadResult result)
    {
        if (marker.Name is null || result.Class != ObservationClass.Name || result.Name is not { Length: > 0 } read || result.LetterMargins is not { } margins
            || LookAlikes.FoldIdentity(read) == LookAlikes.FoldIdentity(marker.Name) || _names.IsNotFound(read) || _judge.Classify(read) != NameClass.Valid)
            return 0;
        return IdentityEvidence.StrongDifferences(marker.Name, read, margins, _settings.StrongLetterMarginNats);
    }

    private static string? MarginsText(IReadOnlyList<double>? margins) =>
        margins is null ? null : string.Join(" ", margins.Select(m => double.IsFinite(m) ? m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "nan"));

    private bool InContact(MarkerRecord marker, IReadOnlyList<MarkerRecord> markers)
    {
        var reach = _settings.BeliefContactLines * marker.Height;
        var box = marker.PlateBox;
        foreach (var other in markers)
        {
            if (other.Id == marker.Id)
                continue;
            var near = other.PlateBox;
            if (near.X - reach < box.X + box.Width && box.X - reach < near.X + near.Width && near.Y - reach < box.Y + box.Height && box.Y - reach < near.Y + near.Height)
                return true;
        }

        return false;
    }

    private bool HasBlob(MarkerRecord marker, ReadResult result) =>
        result.NameLine is { } row && Fingerprint.HasSolidRun(row.Fingerprint, row.Height, _settings.VoteBlobInkShare, _settings.VoteBlobMinWidthLines * marker.Height);

    private (string? Vote, bool Excluded) Vote(MarkerRecord marker, ReadResult result, double frameMs, double freshnessMs)
    {
        if (!Counts(marker, result, frameMs, freshnessMs) || LookAlikes.FoldIdentity(result.Name!) == LookAlikes.FoldIdentity(marker.Name!))
            return (null, false);
        var read = result.Name!;
        var folded = LookAlikes.FoldIdentity(read);
        return _judge.Classify(read) switch
        {
            NameClass.Valid => (folded, false),
            NameClass.Excluded when _judge.IsExclusionListed(read) => (folded, true),
            _ => (null, false),
        };
    }

    private bool Supports(MarkerRecord marker, ReadResult result, double frameMs, double freshnessMs) =>
        Counts(marker, result, frameMs, freshnessMs) && LookAlikes.FoldIdentity(result.Name!) == LookAlikes.FoldIdentity(marker.Name!);

    private bool Counts(MarkerRecord marker, ReadResult result, double frameMs, double freshnessMs) =>
        marker.Name is not null && result.Class == ObservationClass.Name && result.Name is { Length: > 0 } && result.MinLetterMargin >= _settings.VoteMinMarginNats
        && marker.HitNameEvidence == NameEvidence.Whole && Math.Abs(frameMs - marker.HitNameEvidenceMs) <= freshnessMs;

    private void Remeasure(PlateState state, ReadItem item, MarkerRecord marker, ReadResult result, double frameMs, List<IndexUpdate> updates)
    {
        state.RemeasureLeft--;
        var same = result is { Class: ObservationClass.Name, Name: { } read } && state.Name is { } name
                   && string.Equals(LookAlikes.FoldO0(read), LookAlikes.FoldO0(name), StringComparison.OrdinalIgnoreCase);
        var agreeing = 0;
        if (same && result.Anchor is { } anchor && result.Rows is { Count: > 0 } rows)
        {
            var (_, offset, rel, _) = MeasureOffsets(item, anchor, rows);
            var tolerance = Math.Max(_settings.StableWidthMinPx, _settings.StableWidthLines * item.LineHeight);
            var agrees = state.MeasuredNameRel is { } last
                         && Math.Abs(offset.Width - last.Width) <= tolerance
                         && Math.Abs(offset.X + offset.Width / 2.0 - (last.X + last.Width / 2.0)) <= tolerance
                         && Math.Abs(offset.Y - last.Y) <= tolerance;
            state.MeasuredAgreeing = agrees ? state.MeasuredAgreeing + 1 : 1;
            state.MeasuredNameRel = offset;
            agreeing = state.MeasuredAgreeing;
            if (agreeing >= _settings.RestoredRemeasureReads)
            {
                state.RemeasureLeft = 0;
                var nameLine = result.NameLine is { } measured && marker.NameLine is { } stored
                               && Math.Abs(measured.Width - stored.Width) > _settings.TrackWidthGateLines * marker.Height
                    ? measured
                    : null;
                updates.Add(new RemeasureUpdate(PipelineGroup.Reads, frameMs, _epoch, state.Id, state.Name!, offset, rel, nameLine));
            }
        }

        if (Trace.Line(_clock.NowMs, "rd", "remeasure") is { } traced)
        {
            traced.Num("fms", frameMs).Int("mid", state.Id).Str("class", result.Class.ToString()).Str("name", result.Name).Bool("same", same).Int("agreeing", agreeing)
                .Int("left", state.RemeasureLeft).Num("nameW", result.NameLine?.Width ?? double.NaN, 0).Num("storedW", marker.NameLine?.Width ?? double.NaN, 0);
            if (state.MeasuredNameRel is { } nameRel)
                traced.Box("nameRel", nameRel);
            Trace!.Write(traced.Text());
        }
    }

    private void ExcludeDuplicate(string name, int otherId, double frameMs, List<IndexUpdate> updates)
    {
        if (_session.TryAdd(name))
            _log.Write(_wall.UtcNow, $"Session exclusion: {name} (seen on 2+ white nameplates)");
        foreach (var other in _states.Values)
        {
            var named = other.Status == PlateStatus.Confirmed && other.Name is { } known && string.Equals(known, name, StringComparison.OrdinalIgnoreCase);
            if (!named && !(other.Id == otherId && other.Status == PlateStatus.Confirmed))
                continue;
            other.Status = PlateStatus.Stopped;
            updates.Add(new StatusUpdate(PipelineGroup.Reads, frameMs, _epoch, other.Id, PlateStatus.Stopped));
            _stats?.AddStopped();
        }
    }

    private void ReturnNotFoundToReading(IndexSnapshot snapshot, double stamp, int epoch, List<IndexUpdate> updates)
    {
        foreach (var marker in snapshot.Markers)
        {
            if (marker.Status != PlateStatus.Confirmed || marker.Name is not { } name || marker.Identity is not { Rsi: RsiState.NotFound } identity)
                continue;
            if (!string.Equals(identity.Handle, name, StringComparison.OrdinalIgnoreCase))
                continue;
            var known = _states.TryGetValue(marker.Id, out var state);
            if (known && state!.Status == PlateStatus.Stopped)
                continue;
            _states[marker.Id] = new PlateState(marker.Id, known ? state!.BornMs : stamp)
            {
                EverNamed = true,
                ReadsTotal = known ? state!.ReadsTotal : 0,
                IgnoredReads = known ? state!.IgnoredReads : 0,
            };
            updates.Add(new UnconfirmUpdate(PipelineGroup.Reads, stamp, epoch, marker.Id));
        }
    }

    private static double Stamp(IndexSnapshot snapshot, double nowMs) => double.IsFinite(snapshot.NewFrameMs) ? snapshot.NewFrameMs : nowMs;

    private void SyncEpoch(IndexSnapshot snapshot)
    {
        if (snapshot.Epoch == _epoch)
            return;
        _epoch = snapshot.Epoch;
        foreach (var id in _kept.Keys.ToList())
            FlushKept(id);
        _states.Clear();
        _duplicates.Clear();
        _judge.ForgetMemo();
        _pending = null;
    }

    private void ForgetEnded(IndexSnapshot snapshot)
    {
        if (_states.Count == 0)
            return;
        foreach (var id in _states.Keys.ToList())
        {
            if (snapshot.Find(id) is not null)
                continue;
            FlushKept(id);
            _states.Remove(id);
            _duplicates.Forget(id);
        }
    }

    private void KeepRead(CapturedFrame frame, ReadItem item, ReadResult result)
    {
        if (KeepCrop is null || item.Crop.Image.Empty())
            return;
        var unnamed = result.Name is not { Length: > 0 };
        if (unnamed && result.Reason == ReadResult.NoDistanceLine)
            return;
        var offCentre = unnamed && result.Reason == ReadResult.OffCentre;
        if (!_kept.TryGetValue(item.Id, out var list))
            _kept[item.Id] = list = new List<KeptRead>();
        if (list.Count >= _settings.ReadCropsPerPlate)
            return;
        if (offCentre && list.Count(kept => kept.OffCentre) >= _settings.OffCentreCropsPerPlate)
            return;
        var text = result.Name is { Length: > 0 } name ? name : string.IsNullOrEmpty(result.Reason) ? result.Class.ToString() : result.Class + "-" + result.Reason;
        list.Add(new KeptRead(item.Crop.Image.Clone(), text, result.Name is { Length: > 0 } ? result.Name : null, item.Crop.Origin, frame.WindowWidth, frame.WindowHeight, offCentre));
    }

    private void FlushKept(int id)
    {
        var offCentreStop = _offCentreStops.Remove(id);
        if (!_kept.Remove(id, out var list))
            return;
        var handOver = list.Count > 0
                       && (!_states.TryGetValue(id, out var state) || state.Status != PlateStatus.Stopped || state.IgnoredReads >= _settings.IgnoredReadsLimit || offCentreStop);
        if (handOver && list.Exists(kept => kept.Name is { } name && IsUnknown(name, _settings.UnknownReadDistance)))
        {
            handOver = _unknownCropsHanded < _settings.ReadCropsUnknownPerRun;
            if (handOver)
                _unknownCropsHanded += list.Count;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var kept = list[i];
            if (handOver && KeepCrop is { } keep)
            {
                var text = new string(kept.Text.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
                var r = kept.Region;
                keep(kept.Image, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{id}_{i + 1}_{text}_x{r.X}y{r.Y}w{r.Width}h{r.Height}_win{kept.WindowWidth}x{kept.WindowHeight}"));
            }

            kept.Image.Dispose();
        }
    }

    public static bool IsUnknown(string name, int maxEdits)
    {
        const string unknown = "UNKNOWN";
        if (Math.Abs(name.Length - unknown.Length) > maxEdits)
            return false;
        var row = new int[unknown.Length + 1];
        for (var j = 0; j < row.Length; j++)
            row[j] = j;
        for (var i = 1; i <= name.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= unknown.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (char.ToUpperInvariant(name[i - 1]) == unknown[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }

        return row[unknown.Length] <= maxEdits;
    }

    public void DropAllKept()
    {
        foreach (var id in _kept.Keys.ToList())
            DropKept(id);
    }

    private void DropKept(int id)
    {
        _offCentreStops.Remove(id);
        if (!_kept.Remove(id, out var list))
            return;
        foreach (var kept in list)
            kept.Image.Dispose();
    }

    private static PixelRect ClipToCrop(PixelRect box, CropImage crop)
    {
        var left = Math.Max(0, (int)Math.Round(box.X - crop.Origin.X));
        var top = Math.Max(0, (int)Math.Round(box.Y - crop.Origin.Y));
        var right = Math.Min(crop.Image.Width, (int)Math.Round(box.X - crop.Origin.X) + (int)Math.Round(box.Width));
        var bottom = Math.Min(crop.Image.Height, (int)Math.Round(box.Y - crop.Origin.Y) + (int)Math.Round(box.Height));
        if (right <= left || bottom <= top)
            return box;
        return new PixelRect(left + crop.Origin.X, top + crop.Origin.Y, right - left, bottom - top);
    }

    private static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        return PixelRect.FromEdges(left, top, right, bottom);
    }

    private static double Elapsed(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
}
