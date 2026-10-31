using System.Globalization;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public enum LostRefusalReason
{
    NoNameRow,
    OutsidePatch,
    LowFingerprint,
    Width,
    Gap,
    Centre,
}

public readonly record struct LostRefusalSample(
    int MarkerId, string Name, double LostMs, LostRefusalReason Reason, double Score, PixelRect Line, double DistancePx, double LineHeight, PixelRect Patch)
{
    private const string Prefix = "Live detection: lost plate re-finds refused (samples): ";

    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var lost = double.IsNaN(LostMs) ? "?" : string.Create(inv, $"{LostMs:0}");
        var lines = LineHeight > 0 ? DistancePx / LineHeight : 0.0;
        return string.Create(inv,
            $"#{MarkerId} {Name} lost {lost} ms: {ReasonText()}, line {Line.X:0},{Line.Y:0} {Line.Width:0}x{Line.Height:0} at {DistancePx:0} px ({lines:0.0} lh) from prediction, patch {Patch.Width:0}x{Patch.Height:0}");
    }

    private string ReasonText()
    {
        var inv = CultureInfo.InvariantCulture;
        return Reason switch
        {
            LostRefusalReason.OutsidePatch => "name row outside patch",
            LostRefusalReason.LowFingerprint => string.Create(inv, $"low fingerprint {Score:0.00}"),
            LostRefusalReason.Width => "name row width",
            LostRefusalReason.Gap => "name row gap",
            LostRefusalReason.Centre => "name row centre",
            _ => "no name row found",
        };
    }

    public static string? Text(IReadOnlyList<LostRefusalSample> samples) =>
        samples.Count == 0 ? null : Prefix + string.Join("; ", samples.Select(s => s.Describe()));
}
