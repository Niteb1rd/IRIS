
using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;
using Iris.Core.Config;

namespace Iris.App.Services;

public sealed partial class OverlayController
{
    private void PressTimedOverlay()
    {
        var settings = _services.Settings;
        var press = _timedRun.Press(settings.OverlayDisabled, settings.TimedOverlaySeconds, Environment.TickCount64);
        if (press == TimedOverlayPress.Ignored)
            return;

        _toastText = null;
        _toastTimer.Stop();
        _timedShownSeconds = -1;
        _timedTimer.Stop();
        _timedTimer.Start();
        if (press == TimedOverlayPress.Started)
        {
            _services.Log.Write(_services.Clock.UtcNow, $"Overlay on for {settings.TimedOverlaySeconds} seconds (hotkey; not saved)");
            _services.SetTimedOverlay(true);
        }
        Evaluate();
    }

    private void OnTimedTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        if (_timedRun.Expire(now))
        {
            EndTimedRun(expired: true);
            return;
        }

        var left = _timedRun.SecondsLeft(now);
        if (left != _timedShownSeconds)
            Evaluate();
    }

    private void EndTimedRun(bool expired)
    {
        _timedRun.Stop();
        _timedTimer.Stop();
        _timedShownSeconds = -1;
        if (!_services.IsTimedOverlayOn)
            return;
        _services.Log.Write(_services.Clock.UtcNow, expired ? "Timed overlay ended (overlay off again)" : "Timed overlay ended (overlay now on)");
        if (expired && _services.Settings.OverlayDisabled && _testModel is null && _tracker.Current.State == GameWindowState.Ready)
            ShowToast("Overlay OFF");
        _services.SetTimedOverlay(false);
        Evaluate();
    }

    private string? TimedOverlayText()
    {
        if (!_timedRun.IsActive)
            return null;
        _timedShownSeconds = _timedRun.SecondsLeft(Environment.TickCount64);
        return TimedOverlayRun.CountdownText(_timedShownSeconds);
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ClearToast()
    {
        _toastText = null;
        _toastTimer.Stop();
        Evaluate();
    }

    private void OnToastTick(object? sender, EventArgs e) => ClearToast();
}
