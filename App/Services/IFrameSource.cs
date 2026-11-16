using System;
using System.Collections.Generic;
using System.Threading;
using Iris.Core.Pipeline;
using OpenCvSharp;

namespace Iris.App.Services;

public interface IFrameSource : IDisposable
{
    string Name { get; }

    FrameArrival AcquireNextFrame(IntPtr hwnd, int timeoutMs);

    WaitHandle? ArrivalHandle { get; }

    WaitHandle? CompletionHandle { get; }

    bool CanTakeFrame { get; }

    bool CanTakeScan { get; }

    void ReleaseHeldFrame();

    CopyIssue IssueHeldFrameCopies(IReadOnlyList<Rect> regions, bool firstIsScan);

    ReadbackState Poll(ReadbackTicket ticket);

    ReadbackState ReadInto(ReadbackTicket ticket, IReadOnlyList<Mat> destinations, bool raw, out string? problem);

    void Abandon(ReadbackTicket ticket);

    void Release();
}

public readonly record struct CopyIssue(GrabStatus Status, string? Message, ReadbackTicket Patches, ReadbackTicket Scan, double IssueMs)
{
    public static CopyIssue Unavailable(string message) => new(GrabStatus.Unavailable, message, default, default, 0.0);
}
