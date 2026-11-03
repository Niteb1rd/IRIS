using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Iris.Core.Overlay;

namespace Iris.App.Services;

internal sealed class StampWindow : IDisposable
{
    private const int Scale = 3;
    private const int Margin = 8;

    private readonly Action<string> _warn;
    private LayeredWindow? _window;
    private byte[] _pixels = Array.Empty<byte>();
    private bool _failed;
    private bool _visibleInCapture;

    public StampWindow(Action<string> warn, bool visibleInCapture)
    {
        _warn = warn;
        _visibleInCapture = visibleInCapture;
    }

    public void SetVisibleInCapture(bool visible)
    {
        _visibleInCapture = visible;
        if (_window is not null)
            CaptureExclusion.Apply(_window.Handle, visible);
    }

    public void Show(PixelRect game, string text)
    {
        if (_failed)
            return;
        if (_window is null)
        {
            try
            {
                _window = new LayeredWindow();
                CaptureExclusion.Apply(_window.Handle, _visibleInCapture);
            }
            catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                _failed = true;
                _warn("The developer stamp window could not be created (" + ex.Message + ")");
                return;
            }
        }

        var (width, height) = StampFont.Measure(text, Scale);
        var surfaceWidth = (width + 63) / 64 * 64;
        var bits = _window.BeginSurface(surfaceWidth, height);
        if (bits == IntPtr.Zero)
            return;
        var stride = _window.Stride;
        if (_pixels.Length != stride * height)
            _pixels = new byte[stride * height];
        else
            Array.Clear(_pixels);
        StampFont.Draw(_pixels, stride, width, height, text, Scale);
        Marshal.Copy(_pixels, 0, bits, _pixels.Length);
        var x = (int)Math.Round(game.X) + Margin;
        var y = (int)Math.Round(game.Y + game.Height) - height - Margin;
        if (!_window.Commit(x, y, width, height))
            return;
        if (!_window.IsShown)
        {
            _window.Show();
            _window.AssertTopmost();
        }
    }

    public void Hide() => _window?.Hide();

    public void Dispose()
    {
        _window?.Hide();
        _window?.Dispose();
        _window = null;
    }
}
