using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.Core.Config;

namespace Iris.App.Services;

internal sealed class ConfigFileWatcher : IDisposable
{
    private readonly AppServices _services;
    private readonly Func<bool> _canPoll;
    private readonly Func<ConfigPoll, bool> _handOver;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _polling;
    private bool _disposed;

    public ConfigFileWatcher(AppServices services, Func<bool> canPoll, Func<ConfigPoll, bool> handOver)
    {
        _services = services;
        _canPoll = canPoll;
        _handOver = handOver;
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_polling || _disposed || !_canPoll())
            return;
        _polling = true;
        try
        {
            var poll = await Task.Run(() => _services.PollConfigFiles());
            if (!_disposed && !_handOver(poll))
                _services.ApplyPoll(poll);
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Checking the config files failed: " + ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
