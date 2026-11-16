using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Media;
using Iris.Core;
using Iris.Core.Overlay;

namespace Iris.App.Services;

public sealed class UiSoundPlayer : IDisposable
{
    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly Dictionary<UiSound, (int Volume, SoundPlayer Player)> _players = new();
    private bool _failureLogged;

    public UiSoundPlayer(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
    }

    public void Play(UiSound sound, int volumePercent)
    {
        if (volumePercent <= 0)
            return;
        try
        {
            if (!_players.TryGetValue(sound, out var entry) || entry.Volume != volumePercent)
            {
                if (_players.Remove(sound))
                {
                    entry.Player.Stop();
                    entry.Player.Dispose();
                }
                var player = new SoundPlayer(new MemoryStream(UiSounds.Wav(sound, volumePercent)));
                player.Load();
                entry = (volumePercent, player);
                _players[sound] = entry;
            }
            entry.Player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or Win32Exception)
        {
            if (_failureLogged)
                return;
            _failureLogged = true;
            _log.Write(_clock.UtcNow, "Interactive Overlay: a sound could not be played: " + ex.Message);
        }
    }

    public void Dispose()
    {
        foreach (var (_, player) in _players.Values)
        {
            player.Stop();
            player.Dispose();
        }
        _players.Clear();
    }
}
