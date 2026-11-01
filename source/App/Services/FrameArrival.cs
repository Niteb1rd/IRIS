
using System;

namespace Iris.App.Services;

public enum FrameWait
{
    NewFrame,
    Unchanged,
    Unavailable,
}

public readonly record struct FrameArrival(FrameWait Status, double FrameMs = double.NaN, string? Message = null);
