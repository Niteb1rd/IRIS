using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Iris.Core.Overlay;

namespace Iris.App.Views;

public sealed class CutTextBlock : TextBlock
{
    private const double FitSlack = 0.5;

    private string _full = string.Empty;
    private bool _cutting;
    private bool _checkQueued;

    private (string Text, double Size, FontFamily? Family, FontWeight Weight, double Width) _fullWidth;

    static CutTextBlock()
    {
        TextProperty.OverrideMetadata(typeof(CutTextBlock), new FrameworkPropertyMetadata(OnTextChanged));
    }

    public CutTextBlock()
    {
        SizeChanged += (_, _) => Check();
    }

    public string FullText => _full;

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (CutTextBlock)d;
        if (block._cutting)
            return;
        block._full = e.NewValue as string ?? string.Empty;
        block.QueueCheck();
    }

    private void QueueCheck()
    {
        if (_checkQueued)
            return;
        _checkQueued = true;
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        _checkQueued = false;
        Check();
    }

    private void Check()
    {
        if (TextWrapping != TextWrapping.NoWrap)
        {
            Show(_full);
            return;
        }
        var room = RenderSize.Width - Padding.Left - Padding.Right;
        if (!(room > 0))
            return;
        var shown = Text ?? string.Empty;
        var shownWidth = WidthOf(shown);
        if (shownWidth <= room + FitSlack && (string.Equals(shown, _full, StringComparison.Ordinal) || room <= shownWidth + 1))
            return;
        Show(TextCut.ToWidth(_full, room - FitSlack, WidthOf));
    }

    private void Show(string text)
    {
        if (string.Equals(Text, text, StringComparison.Ordinal))
            return;
        _cutting = true;
        try
        {
            Text = text;
        }
        finally
        {
            _cutting = false;
        }
    }

    private double WidthOf(string text)
    {
        if (!ReferenceEquals(text, _full))
            return MeasureText(text);
        var key = _fullWidth;
        if (ReferenceEquals(key.Text, text) && key.Size == FontSize && Equals(key.Family, FontFamily) && key.Weight == FontWeight)
            return key.Width;
        var width = MeasureText(text);
        _fullWidth = (text, FontSize, FontFamily, FontWeight, width);
        return width;
    }

    private double MeasureText(string text) => new FormattedText(
        text, CultureInfo.CurrentUICulture, FlowDirection, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black,
        VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
