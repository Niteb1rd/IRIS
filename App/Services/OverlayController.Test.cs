
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
    public string StartTest(AppSettings settings, Window anchor, TestOverlayPart part)
    {
        TestPart = part;
        string where;
        if (_tracker.TryFindGameWindow(out var gameBounds))
        {
            _testBounds = gameBounds;
            where = "over the Star Citizen window";
        }
        else
        {
            var handle = new WindowInteropHelper(anchor).Handle;
            if (!NativeMethods.TryGetMonitorBounds(handle, out var monitorBounds))
                throw new InvalidOperationException("Windows did not report the size of the screen.");
            _testBounds = monitorBounds;
            where = "over this screen (Star Citizen is not running)";
        }

        _testModel = BuildTestModel(settings);
        _testSettings = settings;
        _testEndTick = Environment.TickCount64 + TestSeconds * 1000L;
        _testTimer.Start();
        _services.Log.Write(_services.Clock.UtcNow, $"Test overlay started {where} ({_testBounds.Width}x{_testBounds.Height})");

        Evaluate();
        TestStateChanged?.Invoke(this, EventArgs.Empty);
        return where;
    }

    public void StopTest()
    {
        if (_testModel is null)
            return;
        _testModel = null;
        _testSettings = null;
        _testTimer.Stop();
        _services.Log.Write(_services.Clock.UtcNow, "Test overlay ended");

        Evaluate();
        TestStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateTestSettings(AppSettings settings)
    {
        if (_testModel is null)
            return;
        _testSettings = settings;
        _testModel = BuildTestModel(settings);
        Evaluate();
    }

    private OverlayModel BuildTestModel(AppSettings settings) =>
        TestOverlayBuilder.Build(_services.CurrentConfig, _testBounds.Width, _testBounds.Height, TrackingBoundary.From(settings), TestPart, settings);

    private void OnTestTick(object? sender, EventArgs e)
    {
        if (TestSecondsLeft <= 0)
            StopTest();
        else
            TestStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
