using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public readonly record struct TrackSample(double Ms, double X, double Y);

public sealed record MarkerIdentity(string Handle, MatchKind Kind, OverlayColor Color, string OrgsLine, RsiState Rsi = RsiState.Pending);

public sealed record MarkerRecord(int Id, double BornMs)
{
    public PlateStatus Status { get; init; } = PlateStatus.Unconfirmed;

    public double StatusMs { get; init; } = double.NegativeInfinity;

    public double CentreX { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double DistanceWidth { get; init; }

    public double LineMs { get; init; } = double.NegativeInfinity;

    public double LastFoundMs { get; init; } = double.NegativeInfinity;

    public TrackSample? Hist0 { get; init; }

    public TrackSample? Hist1 { get; init; }

    public TrackSample? Post0 { get; init; }

    public TrackSample? Post1 { get; init; }

    public bool MarkedConfirmed { get; init; }

    public string? Name { get; init; }

    public double NameMs { get; init; } = double.NegativeInfinity;

    public PixelRect? PlateRel { get; init; }

    public PixelRect? NameRel { get; init; }

    public double OffsetMs { get; init; } = double.NegativeInfinity;

    public NameLineInfo? NameLine { get; init; }

    public PlateColor? PlateColour { get; init; }

    public double PlateColourMs { get; init; } = double.NegativeInfinity;

    public MarkerIdentity? Identity { get; init; }

    public double IdentityMs { get; init; } = double.NegativeInfinity;

    public PipelineGroup? LineBy { get; init; }

    public PipelineGroup? SamplesBy { get; init; }

    public PipelineGroup? StatusBy { get; init; }

    public PipelineGroup? OffsetBy { get; init; }

    public PipelineGroup? IdentityBy { get; init; }

    public PipelineGroup? PlateColourBy { get; init; }

    public static MarkerRecord Born(int id, PixelRect line, double ms, PipelineGroup? by = null) => new(id, ms)
    {
        StatusMs = ms,
        CentreX = line.X + line.Width / 2.0,
        Top = line.Y,
        Width = line.Width,
        DistanceWidth = line.Width,
        Height = line.Height,
        LineMs = ms,
        LastFoundMs = ms,
        LineBy = by,
        StatusBy = by,
    };

    public double X => CentreX - Width / 2.0;

    public double Y => Top;

    public PixelRect Line => new(X, Top, Width, Height);

    public PixelRect PlateBox => PlateRel is { } rel ? new PixelRect(CentreX + rel.X, Top + rel.Y, rel.Width, rel.Height) : Line;

    public double SamplesMs => Hist1?.Ms ?? double.NegativeInfinity;

    public bool HasTrackingFix => Post1 is not null;

    public double? TrackingIntervalMs => Post0 is { } a && Post1 is { } b ? b.Ms - a.Ms : null;

    public (double VxPerMs, double VyPerMs) Speed() => Velocity(Post0, Post1);

    public (double Vx, double Vy) TrackingVelocity() => Velocity(Hist0, Hist1);

    public bool IsLost(double nowMs, PipelineSettings settings) =>
        Status == PlateStatus.Confirmed && NameLine is not null && nowMs - LastFoundMs > settings.LostAfterMs;

    public (double X, double Y, double Margin, double DtSec) Predict(double nowMs, PipelineSettings settings, bool lostGrowth = true)
    {
        var dt = Math.Max(0.0, (nowMs - LineMs) / 1000.0);
        var (vx, vy) = Velocity(Hist0, Hist1);
        var lookMs = Math.Min(dt * 1000.0, settings.MaxLookAheadMs);
        var px = X + vx * lookMs;
        var py = Top + vy * lookMs;
        var displacement = Math.Sqrt(vx * lookMs * (vx * lookMs) + vy * lookMs * (vy * lookMs));
        var lost = lostGrowth && IsLost(nowMs, settings);
        var growPerSec = lost ? Math.Max(settings.SearchGrowLinesPerSec, settings.LostSearchGrowLinesPerSec) : settings.SearchGrowLinesPerSec;
        var maxLines = lost ? Math.Max(settings.SearchMaxLines, settings.LostSearchMaxLines) : settings.SearchMaxLines;
        var margin = Math.Min(Height * (settings.SearchBaseLines + growPerSec * dt), Height * maxLines) + settings.VelocityGain * displacement;
        return (px, py, margin, dt);
    }

    public MarkerRecord WithLine(double ms, double centreX, double top, double width, double distanceWidth = double.NaN, PipelineGroup? by = null) =>
        this with { CentreX = centreX, Top = top, Width = width, DistanceWidth = double.IsFinite(distanceWidth) ? distanceWidth : width, LineMs = ms, LineBy = by ?? LineBy };

    public MarkerRecord WithLineBox(double ms, PixelRect line, PipelineGroup? by = null) =>
        this with { CentreX = line.X + line.Width / 2.0, Top = line.Y, Width = line.Width, DistanceWidth = line.Width, Height = line.Height, LineMs = ms, LineBy = by ?? LineBy };

    public MarkerRecord WithSamples(double ms, double centreX, double top, PipelineGroup? by = null)
    {
        var sample = new TrackSample(ms, centreX, top);
        var record = this with { Hist0 = Hist1, Hist1 = sample, SamplesBy = by ?? SamplesBy };
        return MarkedConfirmed ? record with { Post0 = Post1, Post1 = sample } : record;
    }

    public MarkerRecord WithFound(double ms) => ms > LastFoundMs ? this with { LastFoundMs = ms } : this;

    public MarkerRecord AfterHit(double ms, double centreX, double top, double width, double distanceWidth = double.NaN) =>
        WithLine(ms, centreX, top, width, distanceWidth).WithSamples(ms, centreX, top).WithFound(ms);

    private static (double Vx, double Vy) Velocity(TrackSample? previous, TrackSample? last)
    {
        if (previous is not { } a || last is not { } b)
            return (0.0, 0.0);
        var dt = b.Ms - a.Ms;
        if (!(dt > 0.0))
            return (0.0, 0.0);
        return ((b.X - a.X) / dt, (b.Y - a.Y) / dt);
    }
}
