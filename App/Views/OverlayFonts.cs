using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Iris.App.Views;

internal static class OverlayFonts
{
    private const string QuanticoFamilyUri = "pack://application:,,,/Assets/#Quantico";
    private const string FallbackFamily = "Segoe UI";

    private static Typeface? _bold;
    private static Typeface? _regular;

    public static Typeface Bold => _bold ??= Load(FontWeights.Bold) ?? new Typeface(new FontFamily(FallbackFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static Typeface Regular => _regular ??= Load(FontWeights.Normal) ?? Bold;

    private static Typeface? Load(FontWeight weight)
    {
        try
        {
            var quantico = new Typeface(new FontFamily(QuanticoFamilyUri), FontStyles.Normal, weight, FontStretches.Normal);
            if (quantico.TryGetGlyphTypeface(out var glyphs)
                && glyphs.Weight == weight
                && glyphs.FamilyNames.Values.Any(name => name.IndexOf("Quantico", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return quantico;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }
}
