using System;
using System.Collections.Generic;
using System.Threading;

namespace Iris.App.Services;

internal sealed class PreciseWait : IDisposable
{
    private readonly HighResolutionTimer? _timer = HighResolutionTimer.TryCreate(plainWithRaisedPeriod: false);
    private WaitHandle[] _handles = Array.Empty<WaitHandle>();
    private WaitHandle[] _withTimer = Array.Empty<WaitHandle>();
    private WaitHandle? _wake;
    private WaitHandle? _alsoWake;
    private bool _prepared;

    public bool Wait(double ms, WaitHandle? wake, WaitHandle? alsoWake = null)
    {
        Prepare(wake, alsoWake);
        if (_timer is null || !_timer.ArmIn((long)(ms * 10_000.0)))
        {
            var timeout = Math.Max(1, (int)Math.Ceiling(ms));
            if (_handles.Length == 0)
            {
                Thread.Sleep(timeout);
                return false;
            }

            return WaitHandle.WaitAny(_handles, timeout) != WaitHandle.WaitTimeout;
        }

        var index = WaitHandle.WaitAny(_withTimer, Math.Max(2, (int)Math.Ceiling(ms * 4)));
        return index >= 0 && index < _handles.Length;
    }

    public void Dispose() => _timer?.Dispose();

    private void Prepare(WaitHandle? wake, WaitHandle? alsoWake)
    {
        if (_prepared && wake == _wake && alsoWake == _alsoWake)
            return;
        _prepared = true;
        _wake = wake;
        _alsoWake = alsoWake;
        var handles = new List<WaitHandle>(3);
        if (wake is not null)
            handles.Add(wake);
        if (alsoWake is not null)
            handles.Add(alsoWake);
        _handles = handles.ToArray();
        if (_timer is not null)
            handles.Add(_timer.Handle);
        _withTimer = handles.ToArray();
    }
}
