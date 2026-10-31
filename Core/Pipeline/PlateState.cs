using Iris.Core.Detection;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public enum PlateStatus
{
    Unconfirmed,
    Unclear,
    Confirmed,
    Stopped,
}

public enum ReadKind
{
    Name,
    Excluded,
    Contact,
    Stop,
    Unclear,
}

public enum ObservationClass
{
    Name,
    Stop,
    Unclear,
}

public readonly record struct ReadRecord(double Ms, long Frame, ReadKind Kind, string? Name, double NameWidth);

public sealed record ReadObservation(
    double Ms,
    long Frame,
    ObservationClass Class,
    string? Name,
    IReadOnlyList<O0Glyph> O0,
    double NameWidth,
    PlateColor? Color,
    PixelRect? NameBox,
    string Reason,
    double LineHeight);

public sealed class PlateState
{
    private readonly List<ReadRecord> _recent = new();

    public PlateState(int id, double bornMs)
    {
        Id = id;
        BornMs = bornMs;
    }

    public int Id { get; }

    public double BornMs { get; }

    public PlateStatus Status { get; set; } = PlateStatus.Unconfirmed;

    public string? Name { get; set; }

    public string? StopReason { get; set; }

    public bool EverNamed { get; set; }

    public int ReadsTotal { get; set; }

    public int IgnoredReads { get; set; }

    public double LastReadMs { get; set; } = double.NegativeInfinity;

    public O0EvidenceTable Evidence { get; } = new();

    public IReadOnlyList<ReadRecord> Recent => _recent;

    public void Record(ReadRecord record, int keep)
    {
        _recent.Add(record);
        while (_recent.Count > keep)
            _recent.RemoveAt(0);
    }
}
