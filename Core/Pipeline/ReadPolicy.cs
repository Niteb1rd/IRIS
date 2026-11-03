using Iris.Core.Detection;

namespace Iris.Core.Pipeline;

public enum NameClass
{
    Valid,
    Excluded,
    Contact,
}

public interface IReadJudge
{
    NameClass Classify(string name);

    bool IsListed(string name);
}

public enum PolicyEvent
{
    None,
    Confirmed,
    Stopped,
}

public sealed record PolicyResult(PolicyEvent Event, ReadKind Kind, string? Name, int? DuplicateOf, bool Restored = false);

public readonly record struct ReadCandidate(int Id, bool Unclear, double CentreDistance, double LastReadMs);

public static class ReadScheduler
{
    public static IReadOnlyList<int> Select(IReadOnlyList<ReadCandidate> candidates, int budget)
    {
        var fresh = candidates.Where(c => !c.Unclear).OrderBy(c => c.CentreDistance).ThenBy(c => c.Id);
        var unclear = candidates.Where(c => c.Unclear).OrderBy(c => c.LastReadMs).ThenBy(c => c.Id);
        return fresh.Concat(unclear).Take(Math.Max(0, budget)).Select(c => c.Id).ToList();
    }
}

public sealed class ReadPolicy
{
    private readonly PipelineSettings _settings;
    private readonly INameResolver _resolver;
    private readonly IReadJudge _judge;
    private readonly WhiteDuplicateRule _duplicates;
    private readonly SessionNames _names;

    public ReadPolicy(PipelineSettings settings, INameResolver resolver, IReadJudge judge, WhiteDuplicateRule duplicates, SessionNames? names = null)
    {
        _settings = settings;
        _resolver = resolver;
        _judge = judge;
        _duplicates = duplicates;
        _names = names ?? new SessionNames();
    }

    public PolicyResult Apply(PlateState plate, ReadObservation observation, long tick, IReadOnlyList<SessionMemoryEntry>? memory = null)
    {
        plate.ReadsTotal++;
        plate.LastReadMs = observation.Ms;
        var firstName = !plate.EverNamed;
        var kind = ReadKind.Unclear;
        string? name = null;
        string? restored = null;
        var decided = false;
        int? duplicateOf = null;
        switch (observation.Class)
        {
            case ObservationClass.Name:
            {
                var resolved = _resolver.Resolve(plate, observation.Name!, observation.O0);
                name = resolved.Name;
                decided = resolved.Decided;
                var verified = _names.SpellingOf(name);
                if (verified != name)
                {
                    name = verified;
                    decided = true;
                }

                if (firstName && Remembered(memory, name, observation.LineHeight) is { } remembered)
                {
                    restored = remembered;
                    name = remembered;
                    decided = true;
                }

                plate.EverNamed = true;
                if (_names.IsNotFound(name))
                    return IgnoreNotFound(plate);
                kind = _judge.Classify(name) switch
                {
                    NameClass.Excluded => ReadKind.Excluded,
                    NameClass.Contact => ReadKind.Contact,
                    _ => ReadKind.Name,
                };
                if (kind == ReadKind.Name && observation.NameBox is { } box && !_judge.IsListed(name))
                {
                    duplicateOf = _duplicates.Note(plate.Id, name, tick, observation.Color == PlateColor.White, box);
                    if (duplicateOf is not null)
                        kind = ReadKind.Excluded;
                }

                break;
            }

            case ObservationClass.Stop:
                kind = plate.EverNamed ? ReadKind.Unclear : ReadKind.Stop;
                break;
        }

        plate.Record(new ReadRecord(observation.Ms, observation.Frame, kind, name, observation.NameWidth), _settings.MaxPlateReadHistory);
        if (plate.Status is PlateStatus.Confirmed or PlateStatus.Stopped)
            return new PolicyResult(PolicyEvent.None, kind, name, duplicateOf);

        plate.Status = kind == ReadKind.Unclear ? PlateStatus.Unclear : PlateStatus.Unconfirmed;

        if (kind == ReadKind.Name && restored is not null)
        {
            plate.Status = PlateStatus.Confirmed;
            plate.Name = name;
            return new PolicyResult(PolicyEvent.Confirmed, kind, name, duplicateOf, true);
        }

        var recent = plate.Recent;
        var n = _settings.ConfirmReads;
        if (kind == ReadKind.Name && recent.Count >= n && IsConfirmable(recent, n, observation.LineHeight))
        {
            if (!decided)
                return new PolicyResult(PolicyEvent.None, kind, name, duplicateOf);
            plate.Status = PlateStatus.Confirmed;
            plate.Name = name;
            return new PolicyResult(PolicyEvent.Confirmed, kind, name, duplicateOf);
        }

        var s = _settings.StopReads;
        if (recent.Count >= s)
        {
            var last = recent[^1].Kind;
            if (last is ReadKind.Excluded or ReadKind.Contact or ReadKind.Stop)
            {
                var all = true;
                for (var i = recent.Count - s; i < recent.Count; i++)
                    all &= recent[i].Kind == last;
                if (all)
                {
                    plate.Status = PlateStatus.Stopped;
                    plate.StopReason = last.ToString();
                    return new PolicyResult(PolicyEvent.Stopped, kind, name, duplicateOf);
                }
            }
        }

        return new PolicyResult(PolicyEvent.None, kind, name, duplicateOf);
    }

    private PolicyResult IgnoreNotFound(PlateState plate)
    {
        plate.IgnoredReads++;
        if (plate.Status is not (PlateStatus.Confirmed or PlateStatus.Stopped) && plate.IgnoredReads >= _settings.IgnoredReadsLimit)
        {
            plate.Status = PlateStatus.Stopped;
            plate.StopReason = "NotFound";
            return new PolicyResult(PolicyEvent.Stopped, ReadKind.Unclear, null, null);
        }

        return new PolicyResult(PolicyEvent.None, ReadKind.Unclear, null, null);
    }

    private string? Remembered(IReadOnlyList<SessionMemoryEntry>? memory, string name, double lineHeight)
    {
        if (memory is null)
            return null;
        var fold = LookAlikes.FoldO0(name);
        string? spelling = null;
        var ambiguous = false;
        foreach (var entry in memory)
        {
            if (entry.LineHeight <= 0.0 || Math.Abs(entry.LineHeight - lineHeight) > Math.Max(_settings.LineHeightMinTolerancePx, _settings.LineHeightTolerance * lineHeight))
                continue;
            if (entry.Name == name)
                return name;
            if (LookAlikes.FoldO0(entry.Name) != fold)
                continue;
            ambiguous |= spelling is not null && spelling != entry.Name;
            spelling = entry.Name;
        }

        return ambiguous ? null : spelling;
    }

    private bool IsConfirmable(IReadOnlyList<ReadRecord> recent, int n, double lineHeight)
    {
        var first = recent[recent.Count - n];
        var frames = new HashSet<long>();
        double minWidth = double.MaxValue, maxWidth = double.MinValue;
        for (var i = recent.Count - n; i < recent.Count; i++)
        {
            var r = recent[i];
            if (r.Kind != ReadKind.Name || r.Name != first.Name)
                return false;
            frames.Add(r.Frame);
            minWidth = Math.Min(minWidth, r.NameWidth);
            maxWidth = Math.Max(maxWidth, r.NameWidth);
        }

        if (frames.Count != n || recent[^1].Ms - first.Ms < _settings.ConfirmSpanMs - 1e-6)
            return false;
        return maxWidth - minWidth <= Math.Max(_settings.StableWidthMinPx, _settings.StableWidthLines * lineHeight);
    }
}
