
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Iris.App.Services;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Views;

public sealed partial class InteractiveWindow
{
    private static readonly IEasingFunction Ease = FrozenEase();

    private FrameworkElement? _listFrame;
    private TranslateTransform? _listShift;
    private UIElement? _listHeader;
    private Panel? _listRows;
    private long _dossierOpenedMs = long.MinValue;
    private ContextMenu? _openMenu;
    private Brush? _scrim;

    private void ShowMenu(ContextMenu menu)
    {
        _openMenu = menu;
        menu.IsOpen = true;
    }

    private static IEasingFunction FrozenEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    private double PlayPanelShift() => (PanelLayout.FrameLeft(_host.Settings, _bounds.Width) - PanelPx().Left) / Scale;

    private void PrepareOpen()
    {
        _scrim = GlassParts.Scrim.Clone();
        _scrim.Opacity = 0;
        _stage.Background = _scrim;
        if (_listFrame is not null)
        {
            _listShift = new TranslateTransform(PlayPanelShift(), 0);
            _listFrame.RenderTransform = _listShift;
        }
        if (_listHeader is not null)
            _listHeader.Opacity = 0;
        if (_searchHost is not null)
            _searchHost.Opacity = 0;
        if (_listRows is not null)
        {
            foreach (UIElement row in _listRows.Children)
                row.Opacity = 0;
        }
    }

    private void AnimateOpen()
    {
        if (_closing || _fading)
            return;
        _scrim?.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, Ms(PanelMotion.ScrimFadeMs)));

        if (_listShift is not null)
            _listShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(PlayPanelShift(), 0, Ms(PanelMotion.SlideMs)) { EasingFunction = Ease });

        if (_listHeader is not null)
            FadeRise(_listHeader, PanelMotion.BodyFadeStartMs, PanelMotion.BodyFadeEndMs - PanelMotion.BodyFadeStartMs, 0);
        if (_searchHost is not null)
            FadeRise(_searchHost, PanelMotion.BodyFadeStartMs, PanelMotion.BodyFadeEndMs - PanelMotion.BodyFadeStartMs, 0);

        if (_listRows is not null)
        {
            for (var i = 0; i < _listRows.Children.Count; i++)
                Flicker(_listRows.Children[i], PanelMotion.RowBeginMs(i));
        }
    }

    private void AnimateDossier(UIElement dossier, bool samePlayer)
    {
        var now = (long)StopwatchClock.Shared.NowMs;
        if (!samePlayer)
            _dossierOpenedMs = now;
        else if (_dossierOpenedMs == long.MinValue || now - _dossierOpenedMs >= PanelMotion.DossierMs)
            return;
        FadeRise(dossier, 0, PanelMotion.DossierMs, now - _dossierOpenedMs);
    }

    private void AnimateClose()
    {
        if (_searchPopup is not null)
            _searchPopup.IsOpen = false;
        if (_openMenu is not null)
            _openMenu.IsOpen = false;
        _stage.IsHitTestVisible = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            NativeMethods.MakeNoActivate(handle);
        var fade = new DoubleAnimation(0, Ms(PanelMotion.CloseMs));
        fade.Completed += (_, _) => Close();
        _stage.BeginAnimation(OpacityProperty, fade);
        _listShift?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(PlayPanelShift(), Ms(PanelMotion.CloseMs)) { EasingFunction = Ease });
    }

    private void FadeRise(UIElement element, double beginMs, double lengthMs, double elapsedMs)
    {
        var rise = PanelMotion.BodyRisePx * Look;
        var opacity = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(-elapsedMs) };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs + lengthMs))));
        var shift = new TranslateTransform(0, rise);
        element.RenderTransform = shift;
        var move = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(-elapsedMs) };
        move.KeyFrames.Add(new LinearDoubleKeyFrame(rise, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        move.KeyFrames.Add(new LinearDoubleKeyFrame(rise, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs))));
        move.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs + lengthMs)), Ease));
        element.BeginAnimation(OpacityProperty, opacity);
        shift.BeginAnimation(TranslateTransform.YProperty, move);
    }

    private static void Flicker(UIElement element, double beginMs)
    {
        var third = PanelMotion.RowFlickerMs / 3;
        var flicker = new DoubleAnimationUsingKeyFrames();
        flicker.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flicker.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs))));
        flicker.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs + third))));
        flicker.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs + 2 * third))));
        flicker.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beginMs + 3 * third))));
        element.BeginAnimation(OpacityProperty, flicker);
    }
}
