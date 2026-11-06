using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Iris.Core.Config;
using Iris.Core.Overlay;

namespace Iris.App.Views;

public sealed class OverlayColorToBrushConverter : IValueConverter
{
    private const byte GameBackgroundR = 0x14;
    private const byte GameBackgroundG = 0x16;
    private const byte GameBackgroundB = 0x1C;

    private static readonly Brush[] Swatches = MakeSwatches();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is OverlayColor color && (int)color >= 0 && (int)color < Swatches.Length ? Swatches[(int)color] : Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static Brush[] MakeSwatches()
    {
        var swatches = new Brush[Enum.GetValues<OverlayColor>().Length];
        foreach (var color in Enum.GetValues<OverlayColor>())
        {
            var (r, g, b) = OverlayPalette.Rgb(color);
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(Blend(r, GameBackgroundR), Blend(g, GameBackgroundG), Blend(b, GameBackgroundB)));
            brush.Freeze();
            swatches[(int)color] = brush;
        }
        return swatches;
    }

    private static byte Blend(byte colour, byte background) =>
        (byte)Math.Round(colour * BracketRasterizer.Opacity + background * (1 - BracketRasterizer.Opacity));
}
