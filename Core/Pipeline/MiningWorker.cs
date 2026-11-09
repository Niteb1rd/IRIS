using System.Diagnostics;
using Iris.Core.Mining;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed class MiningWorker : IDisposable
{
    private sealed record PendingTick(long Tag, double LockedMs);

    private readonly MiningSettings _settings;
    private readonly PipelineSettings _pipe;
    private readonly MiningSignalReader _reader;
    private readonly MiningTableSource? _tables;
    private readonly IUpdateSink _sink;
    private readonly IMonotonicClock _clock;
    private readonly PipelineStats? _stats;
    private MiningLabelState? _state;
    private LabelColours? _colours;
    private readonly FrameAverage _average;
    private readonly DigitEvidence _evidence;
    private readonly LockGeometry _geometry;
    private int? _lastConfident;
    private double _stateLockedMs = double.NaN;
    private PendingTick? _pending;
    private long _tick;
    private int _epoch = int.MinValue;

    public MiningWorker(
        MiningSettings settings, PipelineSettings pipe, MiningSignalReader reader, MiningTableSource? tables, IUpdateSink sink, IMonotonicClock clock, PipelineStats? stats = null)
    {
        _settings = settings;
        _pipe = pipe;
        _reader = reader;
        _tables = tables;
        _sink = sink;
        _clock = clock;
        _stats = stats;
        _average = new FrameAverage(settings.AverageFrames);
        _evidence = new DigitEvidence(settings.FuseReads);
        _geometry = new LockGeometry(settings);
    }

    public ITraceSink? Trace { get; set; }

    public Action<SignalRead, MiningState?>? Changed { get; set; }

    public Action<Mat, Rect, int, int, SignalRead>? KeepCrop { get; set; }

    private string? _keptText;

    public Action<Mat, Rect, int, int, SignalRead, SignalRead>? KeepTableCrop { get; set; }

    private PerValueCooldown? _tableCrops;

    public Action<LossGroup>? KeepLoss
    {
        get => _loss?.Emit;
        set
        {
            _loss?.Dispose();
            _loss = value is null ? null : new MiningLossCapture(_settings) { Emit = value };
        }
    }

    private MiningLossCapture? _loss;

    public bool HasPending => _pending is not null;

    public bool Accepts(CapturedFrame frame)
    {
        if (_pending is not { } pending)
            return false;
        foreach (var region in frame.Regions)
            if (region.Requester == PipelineGroup.Mining && region.Tag == pending.Tag && region.OneTime)
                return true;
        return false;
    }

    public void Prepare(IndexSnapshot snapshot, PipelineEnvironment env, double nowMs, bool enabled)
    {
        SyncEpoch(snapshot);
        if (snapshot.Mining is not { } locked)
        {
            _pending = null;
            return;
        }

        if (!enabled)
        {
            _pending = null;
            Send(new List<IndexUpdate> { new MiningUpdate(PipelineGroup.Mining, Math.Max(snapshot.NewFrameMs, locked.LockedMs), snapshot.Epoch, null) }, nowMs);
            return;
        }

        if (_pending is not null)
            _stats?.AddSkipped(PipelineGroup.Mining);
        _tick++;
        var area = MiningSignalReader.LockArea(env.WindowWidth, env.WindowHeight, locked.RowTop, _settings);
        if (area.IsEmpty)
            return;
        var madeFrom = snapshot.NewFrameMs;
        var expires = nowMs + RegionPlanner.OneTimeExpiryMs(_pipe.ReadRequestMs, snapshot.NominalFrameIntervalMs, _pipe);
        var spec = new RequestSpec(PipelineGroup.Mining, RequestKind.MiningCrop, area, _tick, madeFrom, nowMs, expires, true);
        _pending = new PendingTick(_tick, locked.LockedMs);
        Send(new List<IndexUpdate> { new RequestUpdate(PipelineGroup.Mining, madeFrom, snapshot.Epoch, 0, spec) }, nowMs);
    }

    public void Process(IndexSnapshot snapshot, CapturedFrame frame)
    {
        SyncEpoch(snapshot);
        if (_pending is not { } pending)
            return;
        _pending = null;
        if (snapshot.Mining is not { } locked || locked.LockedMs != pending.LockedMs)
            return;
        FrameRegion? region = null;
        foreach (var r in frame.Regions)
            if (r.Requester == PipelineGroup.Mining && r.Kind == RequestKind.MiningCrop && r.Tag == pending.Tag && r.OneTime)
                region = r;
        if (region is null || region.Image.Empty())
            return;

        if (_state is null || _stateLockedMs != locked.LockedMs)
        {
            _state = new MiningLabelState(_settings, locked.LockedMs, locked.Value, locked.Label);
            _colours = new LabelColours(_settings, locked.TextColour, locked.BackColour);
            _stateLockedMs = locked.LockedMs;
            _keptText = null;
            ForgetHistory();
            _geometry.Clear();
            _loss?.Clear();
        }

        var started = Stopwatch.GetTimestamp();
        var read = _reader.Read(region.Image, region.Region, frame.WindowWidth, frame.WindowHeight);
        var single = read;
        var source = Improve(ref read, region, frame);
        var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _stats?.AddMiningRead(ms);
        if (KeepCrop is not null && read.State != SignalState.None && read.Text != _keptText)
        {
            _keptText = read.Text;
            KeepCrop(region.Image, region.Region, frame.WindowWidth, frame.WindowHeight, read);
        }

        var table = _tables?.Current(_clock.NowMs) ?? MiningTable.Embedded;
        _loss?.Add(single, region, frame, table);
        var snapped = TableSnap.Apply(read, table, _reader.Settings);
        if (!ReferenceEquals(snapped, read))
        {
            read = snapped;
            source += "+table";
            _tableCrops ??= new PerValueCooldown(_settings.TableCropCooldownMs);
            if (KeepTableCrop is not null && snapped.Value is { } value && _tableCrops.TryTake(value, frame.FrameMs))
                KeepTableCrop(region.Image, region.Region, frame.WindowWidth, frame.WindowHeight, single, snapped);
        }

        var labelBefore = _state.Label;
        var changed = _state.Apply(read, frame.FrameMs, table);
        var confident = SignalConfidence.IsConfident(read, _settings.ConfidentMargin);
        var recoloured = !_state.Ended && confident && _colours!.Add(read.TextColour, read.BackColour, _clock.NowMs);
        var geometry = !_state.Ended && confident && read.State == SignalState.Signal ? _geometry.Add(read.Box, read.RowTop, read.DigitHeight, locked) : null;
        if (Trace.Line(_clock.NowMs, "mng", "read") is { } traced)
            Trace!.Write(traced.Int("seq", frame.Sequence).Num("fms", frame.FrameMs).Num("ms", ms, 2).Str("state", read.State.ToString()).Str("text", read.Text)
                .Num("off", read.OffsetPx, 1).Num("score", read.MinScore, 3).Num("margin", SignalConfidence.MinMargin(read), 3).Str("src", source)
                .Num("dh", read.DigitHeight, 1).Num("lockDh", geometry?.DigitHeight ?? locked.DigitHeight, 1)
                .Str("label", _state.Label).Bool("ended", _state.Ended).Text());
        if (!changed && !recoloured && geometry is null)
            return;

        var next = _state.Ended ? null : locked with { Value = _state.Value, Label = _state.Label, TextColour = _colours!.Text, BackColour = _colours.Back };
        if (next is not null && geometry is { } g)
            next = next with { SignalBox = g.Box, RowTop = g.RowTop, DigitHeight = g.DigitHeight };
        Send(new List<IndexUpdate> { new MiningUpdate(PipelineGroup.Mining, frame.FrameMs, snapshot.Epoch, next) }, _clock.NowMs);
        if (next is null || next.Label != labelBefore)
            Changed?.Invoke(read, next);
        if (_state.Ended)
        {
            _state = null;
            _stateLockedMs = double.NaN;
        }
    }

    private string Improve(ref SignalRead read, FrameRegion region, CapturedFrame frame)
    {
        if (read.State is SignalState.None or SignalState.Disqualified)
        {
            ForgetHistory();
            return "frame";
        }

        var confident = SignalConfidence.IsConfident(read, _settings.ConfidentMargin);
        if (confident && read.Value != _lastConfident)
        {
            ForgetHistory();
            _lastConfident = read.Value;
        }

        _average.Add(region.Image, region.Region);
        var source = "frame";
        if (!confident && _average.Average() is { } mean)
        {
            using (mean)
            {
                var again = _reader.Read(mean, region.Region, frame.WindowWidth, frame.WindowHeight);
                if (again.State == SignalState.Signal && (read.State != SignalState.Signal || SignalConfidence.MinMargin(again) > SignalConfidence.MinMargin(read)))
                {
                    read = again;
                    source = "average";
                }
            }
        }

        _evidence.Add(read);
        if (!SignalConfidence.IsConfident(read, _settings.ConfidentMargin) && _evidence.Fused(read, _reader.Settings.MinDigitScore) is { } fused)
        {
            read = fused;
            source = "fused";
        }

        return source;
    }

    private void ForgetHistory()
    {
        _average.Clear();
        _evidence.Reset();
        _lastConfident = null;
    }

    public void Dispose()
    {
        _average.Dispose();
        _loss?.Dispose();
    }

    private void Send(List<IndexUpdate> updates, double workStartMs) => UpdateTiming.Send(_sink, updates, _clock, workStartMs, double.NaN);

    private void SyncEpoch(IndexSnapshot snapshot)
    {
        if (snapshot.Epoch == _epoch)
            return;
        _epoch = snapshot.Epoch;
        _pending = null;
        ForgetHistory();
        _loss?.Clear();
        _state = null;
        _stateLockedMs = double.NaN;
    }
}
