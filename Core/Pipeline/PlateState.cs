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

    public bool EverNamed { get; set; }

    public int ReadsTotal { get; set; }

    public int IgnoredReads { get; set; }

    public double LastReadMs { get; set; } = double.NegativeInfinity;

    public O0EvidenceTable Evidence { get; } = new();

    public int RemeasureLeft { get; set; }

    public PixelRect? MeasuredNameRel { get; set; }

    public int MeasuredAgreeing { get; set; }

    public bool Disputed { get; set; }

    public double LastVoteMs { get; set; } = double.NegativeInfinity;

    public string? RunName { get; set; }

    public bool RunExcluded { get; set; }

    public int RunVotes { get; set; }

    public int Reserve { get; set; }

    public bool InContact { get; set; }

    public IReadOnlyList<ReadRecord> Recent => _recent;

    public void Record(ReadRecord record, int keep)
    {
        _recent.Add(record);
        while (_recent.Count > keep)
            _recent.RemoveAt(0);
    }
}
