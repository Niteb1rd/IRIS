
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Iris.Core.Pipeline;
using OpenCvSharp;
using SharpGen.Runtime;
using Vc = Vortice.Direct3D;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

public sealed partial class DuplicationFrameSource
{
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

    private static int Bucket(int size) => (size + WgcFrameSource.StagingBucket - 1) / WgcFrameSource.StagingBucket * WgcFrameSource.StagingBucket;

    private void CopyRegion(Vd.ID3D11Texture2D texture, Rect region, Slot slot)
    {
        EnsureStaging(slot, region.Width, region.Height);
        var left = region.X + _heldOffsetX;
        var top = region.Y + _heldOffsetY;
        var box = new Vortice.Mathematics.Box(left, top, 0, left + region.Width, top + region.Height, 1);
        _context!.CopySubresourceRegion(slot.Staging!, 0, 0, 0, 0, texture, 0, box);
    }

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
