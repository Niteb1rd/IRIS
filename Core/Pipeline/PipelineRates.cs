using System.Globalization;
using Iris.Core.Config;

namespace Iris.Core.Pipeline;

public sealed record PipelineRates
{
    public double ScanHz { get; init; }

    public double PlateHz { get; init; }

    public double ConfirmedReadHz { get; init; }

    public double DisputedReadHz { get; init; }

    public double StoppedTrackHz { get; init; }

    public double ConfirmSpanMs { get; init; }

    public int LostSearchEveryFrames { get; init; }

    public static PipelineRates Normal { get; } = Of(PipelineSettings.Default);

    public static PipelineRates High { get; } = new()
    {
        ScanHz = 6.0, PlateHz = 18.0, ConfirmedReadHz = 6.0, DisputedReadHz = 18.0, StoppedTrackHz = 36.0, LostSearchEveryFrames = 3, ConfirmSpanMs = 75.0,
    };

    public static PipelineRates Ultra { get; } = new()
    {
        ScanHz = 8.0, PlateHz = 24.0, ConfirmedReadHz = 8.0, DisputedReadHz = 24.0, StoppedTrackHz = 48.0, LostSearchEveryFrames = 2, ConfirmSpanMs = 75.0,
    };

    public static PipelineRates For(CpuUsage usage) => usage switch
    {
        CpuUsage.High => High,
        CpuUsage.Ultra => Ultra,
        _ => Normal,
    };

    public static PipelineRates Of(PipelineSettings settings) => new()
    {
        ScanHz = settings.ScanHz, PlateHz = settings.PlateHz, ConfirmedReadHz = settings.ConfirmedReadHz, DisputedReadHz = settings.DisputedReadHz,
        StoppedTrackHz = settings.StoppedTrackHz, LostSearchEveryFrames = settings.LostThrottleEveryFrames, ConfirmSpanMs = settings.ConfirmSpanMs,
    };

    public static IReadOnlyList<string> Names { get; } = new[]
    {
        nameof(ScanHz), nameof(PlateHz), nameof(ConfirmedReadHz), nameof(DisputedReadHz), nameof(StoppedTrackHz), nameof(LostSearchEveryFrames), nameof(ConfirmSpanMs),
    };

    public PipelineSettings Apply(PipelineSettings baseSettings)
    {
        var scan = ScanHz / PipelineSettings.Default.ScanHz;
        var reads = PlateHz / PipelineSettings.Default.PlateHz;
        return baseSettings with
        {
            ScanHz = ScanHz,
            PlateHz = PlateHz,
            ConfirmedReadHz = ConfirmedReadHz,
            DisputedReadHz = DisputedReadHz,
            StoppedTrackHz = StoppedTrackHz,
            LostThrottleEveryFrames = LostSearchEveryFrames,
            ConfirmSpanMs = ConfirmSpanMs,
            UnconfirmedCheckFails = Scaled(baseSettings.UnconfirmedCheckFails, scan),
            ConfirmedCheckFails = Scaled(baseSettings.ConfirmedCheckFails, scan),
            ConfirmedNoTrackChecks = Scaled(baseSettings.ConfirmedNoTrackChecks, scan),
            DuplicateWindowTicks = Scaled(baseSettings.DuplicateWindowTicks, reads),
            ConfirmWindowReads = Math.Min(baseSettings.MaxPlateReadHistory,
                Math.Max(Scaled(baseSettings.ConfirmWindowReads, reads), (int)Math.Ceiling(ConfirmSpanMs * PlateHz / 1000.0 - 1e-9) + 1)),
            RestoredRemeasureMaxReads = Scaled(baseSettings.RestoredRemeasureMaxReads, reads),
            IgnoredReadsLimit = Scaled(baseSettings.IgnoredReadsLimit, reads),
            ReadRequestMs = reads > 1.0 ? baseSettings.ReadRequestMs / reads : baseSettings.ReadRequestMs,
            ScanRequestMs = scan > 1.0 ? baseSettings.ScanRequestMs / scan : baseSettings.ScanRequestMs,
        };
    }

    private static int Scaled(int count, double factor) => factor <= 1.0 ? count : (int)Math.Ceiling(count * factor - 1e-9);

    public bool TryParseOverride(string text, out PipelineRates changed, out string? error)
    {
        changed = this;
        error = null;
        var parts = text.Split('=', 2, StringSplitOptions.TrimEntries);
        var name = parts.Length == 2 ? Names.FirstOrDefault(n => n.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (name is null || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !(value > 0) || double.IsInfinity(value))
        {
            error = $"a rate override is Name=Value with Name one of {string.Join(", ", Names)} and a number above 0 (got \"{text}\").";
            return false;
        }

        if (name == nameof(LostSearchEveryFrames) && (value != Math.Floor(value) || value > 1000))
        {
            error = $"{nameof(LostSearchEveryFrames)} needs a whole number from 1 (got \"{parts[1]}\").";
            return false;
        }

        changed = name switch
        {
            nameof(ScanHz) => this with { ScanHz = value },
            nameof(PlateHz) => this with { PlateHz = value },
            nameof(ConfirmedReadHz) => this with { ConfirmedReadHz = value },
            nameof(DisputedReadHz) => this with { DisputedReadHz = value },
            nameof(StoppedTrackHz) => this with { StoppedTrackHz = value },
            nameof(ConfirmSpanMs) => this with { ConfirmSpanMs = value },
            _ => this with { LostSearchEveryFrames = (int)value },
        };
        return true;
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"scan {ScanHz:0.##}/s, plate reads {PlateHz:0.##}/s, identity reads {ConfirmedReadHz:0.##}/s ({DisputedReadHz:0.##}/s in dispute), stopped plates {StoppedTrackHz:0.##}/s, long-lost plates searched every {LostSearchEveryFrames} frame(s), confirmation span {ConfirmSpanMs:0.##} ms");
}
