using System;
using System.Collections.Generic;
using Iris.Core.Pipeline;
using OpenCvSharp;

namespace Iris.App.Services;

public interface IFrameSource : IDisposable
{
    string Name { get; }

    FrameArrival AcquireNextFrame(IntPtr hwnd);

    void ReleaseHeldFrame();

    GrabRegionsResult CopyHeldFrameInto(IReadOnlyList<Rect> regions, bool firstIsScan, IReadOnlyList<Mat> destinations, bool deferScan = false);

    bool ReadPendingScan(out string? problem);

    void Release();

    CopyTimes LastCopyTimes { get; }
}

public readonly record struct CopyTimes(double IssueMs, double ReadMs);
