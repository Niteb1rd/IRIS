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

    public static Typeface Bold => _bold ??= Load();

    private static Typeface Load()
    {
        try
        {
            var quantico = new Typeface(new FontFamily(QuanticoFamilyUri), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            if (quantico.TryGetGlyphTypeface(out var glyphs)
                && glyphs.FamilyNames.Values.Any(name => name.IndexOf("Quantico", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return quantico;
            }
        }
        catch (Exception)
        {
        }

        return new Typeface(new FontFamily(FallbackFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    }
}
