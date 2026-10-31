using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Pipeline;
using OpenCvSharp;
using SharpGen.Runtime;
using Vc = Vortice.Direct3D;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;
using Wg = Windows.Graphics;
using Wgc = Windows.Graphics.Capture;
using Wgd = Windows.Graphics.DirectX;
using Wgd11 = Windows.Graphics.DirectX.Direct3D11;

namespace Iris.App.Services;

public sealed partial class WgcFrameSource : IDisposable
{
    public const int StagingBucket = 64;
    private const int PoolBuffers = 2;

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private sealed class Slot
    {
        public Vd.ID3D11Texture2D? Staging;
        public int StagingWidth;
        public int StagingHeight;
        public Mat? Bgr;
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private readonly List<Slot> _slots = new();
    private readonly Slot _scan = new();
    private Rect _pendingScanRegion;
    private Mat? _pendingScanDestination;
    private readonly AutoResetEvent _arrived = new(false);
    private Vd.ID3D11Device? _device;
    private Vd.ID3D11DeviceContext? _context;
    private Wgd11.IDirect3DDevice? _winrtDevice;
    private Wgc.GraphicsCaptureItem? _item;
    private Wgc.Direct3D11CaptureFramePool? _pool;
    private Wgc.GraphicsCaptureSession? _session;
    private Wg.SizeInt32 _poolSize;
    private volatile bool _itemClosed;
    private IntPtr _hwnd;
    private Wgc.Direct3D11CaptureFrame? _held;
    private Vd.ID3D11Texture2D? _heldTexture;
    private double _heldMs;
    private int _heldOffsetX;
    private int _heldOffsetY;
    private long _retryAtTick;
    private long _lastFrameTick;
    private long _lastCallTick;
    private string? _lastProblem;
    private bool _borderless;
    private bool _disposed;

    public bool HideBorder { get; set; }

    public Action<string>? Log { get; set; }

    public FrameArrival AcquireNextFrame(IntPtr hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseHeldFrame();
        if (hwnd == IntPtr.Zero)
            return Unavailable("there is no game window");
        if (_pool is not null && (_hwnd != hwnd || _itemClosed || _borderless != CaptureBorder.WantsBorderless(HideBorder)))
        {
            Release();
            _retryAtTick = 0;
        }

        var tick = Environment.TickCount64;
        if (tick - _lastCallTick > LiveSettings.CaptureNoFrameRestartMs)
            _lastFrameTick = tick;
        _lastCallTick = tick;
        if (_pool is null)
        {
            if (tick < _retryAtTick)
                return Unavailable(_lastProblem ?? "window capture is not ready");
            string? problem;
            try
            {
                problem = SetUp(hwnd);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
            }

            if (problem is not null)
            {
                Release();
                _retryAtTick = tick + LiveSettings.CaptureRetryAfterFailureMs;
                return Unavailable(problem);
            }

            _hwnd = hwnd;
            _lastFrameTick = tick;
        }

        try
        {
            return Acquire(tick);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Release();
            _retryAtTick = tick + LiveSettings.CaptureRetryAfterFailureMs;
            return Unavailable("window capture failed and will be set up again: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void ReleaseHeldFrame()
    {
        var texture = _heldTexture;
        var frame = _held;
        _heldTexture = null;
        _held = null;
        TryDispose(texture);
        TryDispose(frame);
    }

    public GrabRegionsResult CopyHeldFrameInto(IReadOnlyList<Rect> regions, bool firstIsScan, IReadOnlyList<Mat> destinations, bool deferScan = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destinations.Count != regions.Count)
            throw new ArgumentException("One destination for each region is needed.", nameof(destinations));
        var texture = _heldTexture;
        if (texture is null)
        {
            ReleaseHeldFrame();
            return GrabRegionsResult.Unavailable("no window frame was held");
        }

        _pendingScanDestination = null;
        var frameMs = _heldMs;
        if (regions.Count == 0)
        {
            ReleaseHeldFrame();
            return new GrabRegionsResult(GrabStatus.Ok, Array.Empty<Mat?>(), null, frameMs);
        }

        try
        {
            var description = texture.Description;
            if (description.Format != Vx.Format.B8G8R8A8_UNorm)
            {
                ReleaseHeldFrame();
                Release();
                _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
                return GrabRegionsResult.Unavailable($"the window picture has pixel format {description.Format}, which IRIS cannot read (only 8 bit B8G8R8A8 is supported)");
            }

            var width = (int)description.Width;
            var height = (int)description.Height;
            foreach (var region in regions)
            {
                var left = region.X + _heldOffsetX;
                var top = region.Y + _heldOffsetY;
                if (left < 0 || top < 0 || left + region.Width > width || top + region.Height > height)
                {
                    ReleaseHeldFrame();
                    return GrabRegionsResult.Unavailable($"a capture region is not inside the captured game window ({width}x{height}, client area at {_heldOffsetX},{_heldOffsetY})");
                }
            }

            var slots = AssignSlots(regions, firstIsScan);
            try
            {
                var scanFirst = firstIsScan;
                for (var i = scanFirst ? 1 : 0; i < regions.Count; i++)
                    CopyRegion(texture, regions[i], slots[i]);
                if (scanFirst)
                {
                    if (regions.Count > 1)
                        _context!.Flush();
                    CopyRegion(texture, regions[0], slots[0]);
                }
            }
            finally
            {
                ReleaseHeldFrame();
            }

            var images = new List<Mat?>(regions.Count);
            for (var i = 0; i < regions.Count; i++)
                images.Add(destinations[i]);
            for (var i = firstIsScan ? 1 : 0; i < regions.Count; i++)
                ReadStaging(slots[i], regions[i], destinations[i], false);
            if (firstIsScan)
            {
                if (deferScan)
                {
                    _pendingScanRegion = regions[0];
                    _pendingScanDestination = destinations[0];
                }
                else
                {
                    ReadStaging(slots[0], regions[0], destinations[0], true);
                }
            }

            return new GrabRegionsResult(GrabStatus.Ok, images, null, frameMs);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            ReleaseHeldFrame();
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            return GrabRegionsResult.Unavailable("window capture failed and will be set up again: " + ex.Message);
        }
    }

    private void CopyRegion(Vd.ID3D11Texture2D texture, Rect region, Slot slot)
    {
        EnsureStaging(slot, region.Width, region.Height);
        var left = region.X + _heldOffsetX;
        var top = region.Y + _heldOffsetY;
        var box = new Vortice.Mathematics.Box(left, top, 0, left + region.Width, top + region.Height, 1);
        _context!.CopySubresourceRegion(slot.Staging!, 0, 0, 0, 0, texture, 0, box);
    }

    public bool ReadPendingScan(out string? problem)
    {
        problem = null;
        var destination = _pendingScanDestination;
        _pendingScanDestination = null;
        if (destination is null)
            return true;
        try
        {
            if (_scan.Staging is null || _context is null)
            {
                problem = "the scan region's copy is gone";
                return false;
            }

            ReadStaging(_scan, _pendingScanRegion, destination, true);
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            Release();
            _retryAtTick = Environment.TickCount64 + LiveSettings.CaptureRetryAfterFailureMs;
            problem = "reading the scan region failed and window capture will be set up again: " + ex.Message;
            return false;
        }
    }

    public void Release()
    {
        _pendingScanDestination = null;
        ReleaseHeldFrame();
        foreach (var slot in AllSlots())
        {
            TryDispose(slot.Staging);
            slot.Staging = null;
            slot.StagingWidth = 0;
            slot.StagingHeight = 0;
        }

        var session = _session;
        var pool = _pool;
        var item = _item;
        _session = null;
        _pool = null;
        _item = null;
        try
        {
            if (pool is not null)
                pool.FrameArrived -= OnFrameArrived;
            if (item is not null)
                item.Closed -= OnItemClosed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        TryDispose(session);
        TryDispose(pool);
        TryDispose(_winrtDevice);
        _winrtDevice = null;
        TryDispose(_context);
        _context = null;
        TryDispose(_device);
        _device = null;
        _hwnd = IntPtr.Zero;
        _itemClosed = false;
        _arrived.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Release();
        foreach (var slot in AllSlots())
        {
            slot.Bgr?.Dispose();
            slot.Bgr = null;
        }

        _slots.Clear();
        _disposed = true;
        _arrived.Dispose();
    }

    private IEnumerable<Slot> AllSlots()
    {
        yield return _scan;
        foreach (var slot in _slots)
            yield return slot;
    }

    private Slot[] AssignSlots(IReadOnlyList<Rect> regions, bool firstIsScan)
    {
        var result = new Slot?[regions.Count];
        var used = new HashSet<Slot>();
        var start = 0;
        if (firstIsScan && regions.Count > 0)
        {
            result[0] = _scan;
            used.Add(_scan);
            start = 1;
        }

        for (var i = start; i < regions.Count; i++)
        {
            var width = Bucket(regions[i].Width);
            var height = Bucket(regions[i].Height);
            result[i] = _slots.FirstOrDefault(slot => !used.Contains(slot) && slot.Staging is not null && slot.StagingWidth == width && slot.StagingHeight == height);
            if (result[i] is not null)
                used.Add(result[i]!);
        }

        for (var i = start; i < regions.Count; i++)
        {
            if (result[i] is not null)
                continue;
            var slot = _slots.FirstOrDefault(s => !used.Contains(s));
            if (slot is null)
            {
                slot = new Slot();
                _slots.Add(slot);
            }

            result[i] = slot;
            used.Add(slot);
        }

        return result!;
    }

    private static int Bucket(int size) => (size + StagingBucket - 1) / StagingBucket * StagingBucket;

    private void EnsureStaging(Slot slot, int width, int height)
    {
        var bucketWidth = Bucket(width);
        var bucketHeight = Bucket(height);
        if (slot.Staging is not null && slot.StagingWidth == bucketWidth && slot.StagingHeight == bucketHeight)
            return;
        TryDispose(slot.Staging);
        slot.Staging = null;
        var description = new Vd.Texture2DDescription
        {
            Width = (uint)bucketWidth,
            Height = (uint)bucketHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vx.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vx.SampleDescription(1, 0),
            Usage = Vd.ResourceUsage.Staging,
            BindFlags = Vd.BindFlags.None,
            CPUAccessFlags = Vd.CpuAccessFlags.Read,
            MiscFlags = Vd.ResourceOptionFlags.None,
        };
        slot.Staging = _device!.CreateTexture2D(description);
        slot.StagingWidth = bucketWidth;
        slot.StagingHeight = bucketHeight;
    }

    private void ReadStaging(Slot slot, Rect region, Mat destination, bool raw)
    {
        var staging = slot.Staging!;
        var context = _context!;
        var mapped = context.Map(staging, 0, Vd.MapMode.Read, Vd.MapFlags.None);
        try
        {
            using var source = Mat.FromPixelData(region.Height, region.Width, MatType.CV_8UC4, mapped.DataPointer, mapped.RowPitch);
            if (raw)
                source.CopyTo(destination);
            else
                Cv2.CvtColor(source, destination, ColorConversionCodes.BGRA2BGR);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
