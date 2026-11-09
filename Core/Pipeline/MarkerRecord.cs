using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public readonly record struct TrackSample(double Ms, double X, double Y, bool Template = false);

public sealed record MarkerIdentity(string Handle, MatchKind Kind, OverlayColor Color, PanelOrgs Orgs, RsiState Rsi = RsiState.Pending);

public sealed record MarkerRecord(int Id, double BornMs)
{
    public PlateStatus Status { get; init; } = PlateStatus.Unconfirmed;

    public double StatusMs { get; init; } = double.NegativeInfinity;

    public double CentreX { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double DistanceWidth { get; init; }

    public bool BornWithSpeedGroup { get; init; }

    public double DistanceMetres { get; init; } = double.NaN;

    public double LineMs { get; init; } = double.NegativeInfinity;

    public double LastFoundMs { get; init; } = double.NegativeInfinity;

    public TrackSample? Hist0 { get; init; }

    public TrackSample? Hist1 { get; init; }

    public TrackSample? Post0 { get; init; }

    public TrackSample? Post1 { get; init; }

    public const int RecentSamples = 6;

    public TrackSample[] Recent { get; init; } = Array.Empty<TrackSample>();

    public int MissedFrames { get; init; }

    public double PredictionErrorPx { get; init; }

    public bool MarkedConfirmed { get; init; }

    public string? Name { get; init; }

    public double NameMs { get; init; } = double.NegativeInfinity;

    public double NameSeenMs { get; init; } = double.NegativeInfinity;

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

    public (double VxPerMs, double VyPerMs) Speed(double windowMs = 0.0, double maxPxPerMs = double.PositiveInfinity)
    {
        if (!(windowMs > 0.0))
            return Limit(Velocity(Post0, Post1), maxPxPerMs);
        if (Post1 is null)
            return (0.0, 0.0);
        return Limit(Fit(Recent, windowMs, OffsetMs, hitsOnly: true), maxPxPerMs);
    }

    public (double Vx, double Vy) TrackingVelocity(double windowMs = 0.0, double maxPxPerMs = double.PositiveInfinity) =>
        Limit(windowMs > 0.0 && Hist1 is not { Template: true } ? Fit(Recent, windowMs, double.NegativeInfinity) : Velocity(Hist0, Hist1), maxPxPerMs);

    public double MaxSpeedPxPerMs(PipelineSettings settings) => settings.MaxSpeedLinesPerMs * Height;

    public bool IsLost(double nowMs, PipelineSettings settings) =>
        Status == PlateStatus.Confirmed && NameLine is not null && nowMs - LastFoundMs > settings.LostAfterMs;

    public (double X, double Y, double Margin, double DtSec) Predict(double nowMs, PipelineSettings settings, bool lostGrowth = true)
    {
        var dt = Math.Max(0.0, (nowMs - LineMs) / 1000.0);
        var (vx, vy) = TrackingVelocity(settings.VelocityWindowMs, MaxSpeedPxPerMs(settings));
        var lookMs = Math.Min(dt * 1000.0, settings.MaxLookAheadMs);
        var px = X + vx * lookMs;
        var py = Top + vy * lookMs;
        var displacement = Math.Sqrt(vx * lookMs * (vx * lookMs) + vy * lookMs * (vy * lookMs));
        var lost = lostGrowth && IsLost(nowMs, settings);
        var growPerSec = lost ? Math.Max(settings.SearchGrowLinesPerSec, settings.LostSearchGrowLinesPerSec) : settings.SearchGrowLinesPerSec;
        var maxLines = lost ? settings.LostSearchMaxLines : settings.SearchMaxLines;
        var margin = Math.Min(Height * (settings.SearchBaseLines + growPerSec * dt) + settings.PredictionErrorGain * PredictionErrorPx, Height * maxLines)
            + settings.VelocityGain * displacement;
        return (px, py, margin, dt);
    }

    public MarkerRecord WithLine(double ms, double centreX, double top, double width, double distanceWidth = double.NaN, PipelineGroup? by = null) =>
        this with { CentreX = centreX, Top = top, Width = width, DistanceWidth = double.IsFinite(distanceWidth) ? distanceWidth : width, LineMs = ms, LineBy = by ?? LineBy };

    public MarkerRecord WithLineBox(double ms, PixelRect line, PipelineGroup? by = null) =>
        this with { CentreX = line.X + line.Width / 2.0, Top = line.Y, Width = line.Width, DistanceWidth = line.Width, Height = line.Height, LineMs = ms, LineBy = by ?? LineBy };

    public MarkerRecord WithSamples(double ms, double centreX, double top, PipelineGroup? by = null, bool template = false)
    {
        var sample = new TrackSample(ms, centreX, top, template);
        var recent = new TrackSample[Math.Min(Recent.Length + 1, RecentSamples)];
        Array.Copy(Recent, Recent.Length + 1 - recent.Length, recent, 0, recent.Length - 1);
        recent[^1] = sample;
        var record = this with { Hist0 = Hist1, Hist1 = sample, Recent = recent, SamplesBy = by ?? SamplesBy };
        return MarkedConfirmed && !template ? record with { Post0 = Post1, Post1 = sample } : record;
    }

    public MarkerRecord WithFound(double ms) => ms > LastFoundMs ? this with { LastFoundMs = ms } : this;

    public MarkerRecord AfterHit(double ms, double centreX, double top, double width, double distanceWidth = double.NaN) =>
        WithLine(ms, centreX, top, width, distanceWidth).WithSamples(ms, centreX, top).WithFound(ms);

    private static (double Vx, double Vy) Fit(TrackSample[] samples, double windowMs, double sinceMs, bool hitsOnly = false)
    {
        if (samples.Length < 2)
            return (0.0, 0.0);
        var newest = double.NegativeInfinity;
        foreach (var s in samples)
            if (!(hitsOnly && s.Template))
                newest = Math.Max(newest, s.Ms);
        double n = 0, st = 0, sx = 0, sy = 0;
        foreach (var s in samples)
        {
            if (s.Ms < newest - windowMs || s.Ms < sinceMs || (hitsOnly && s.Template))
                continue;
            n++;
            st += s.Ms;
            sx += s.X;
            sy += s.Y;
        }

        if (n < 2)
            return (0.0, 0.0);
        var mt = st / n;
        var mx = sx / n;
        var my = sy / n;
        double stt = 0, stx = 0, sty = 0;
        foreach (var s in samples)
        {
            if (s.Ms < newest - windowMs || s.Ms < sinceMs || (hitsOnly && s.Template))
                continue;
            var dt = s.Ms - mt;
            stt += dt * dt;
            stx += dt * (s.X - mx);
            sty += dt * (s.Y - my);
        }

        return stt > 0.0 ? (stx / stt, sty / stt) : (0.0, 0.0);
    }

    private static (double Vx, double Vy) Limit((double Vx, double Vy) v, double maxPxPerMs)
    {
        var speed = Math.Sqrt(v.Vx * v.Vx + v.Vy * v.Vy);
        return speed > maxPxPerMs && speed > 0.0 ? (v.Vx * maxPxPerMs / speed, v.Vy * maxPxPerMs / speed) : v;
    }

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
