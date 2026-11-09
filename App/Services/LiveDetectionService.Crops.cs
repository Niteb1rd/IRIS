
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Iris.Core.Lookup;
using Iris.Core.Detection;
using Iris.Core.Mining;
using Iris.Core.Pipeline;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.App.Services;

public sealed partial class LiveDetectionService
{
    private void KeepReadCrop(Mat image, string name)
    {
        if (++_readCropsKept > _pipe.ReadCropsPerRun)
            return;
        var copy = image.Clone();
        var folder = Path.Combine(_services.Paths.LogsFolder, "reads");
        var file = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{name}.png");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Cv2.ImWrite(Path.Combine(folder, file), copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
            }
            finally
            {
                copy.Dispose();
            }
        });
    }

    private void KeepTrackingCrop(Mat image, string name)
    {
        if (++_trackingCropsKept > _pipe.TrackCropsPerRun)
            return;
        var copy = image.Clone();
        var folder = Path.Combine(_services.Paths.LogsFolder, "tracking");
        var file = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{name}.png");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Cv2.ImWrite(Path.Combine(folder, file), copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
            }
            finally
            {
                copy.Dispose();
            }
        });
    }

    private void KeepMiningCrop(Mat image, Rect region, int windowWidth, int windowHeight, SignalRead read)
    {
        if (Interlocked.Increment(ref _miningCropsKept) > _mining.KeptCropsPerRun)
            return;
        var copy = image.Clone();
        var folder = Path.Combine(_services.Paths.LogsFolder, "mining");
        var text = read.Text.Length == 0 ? "none" : read.Text.Replace(",", string.Empty, StringComparison.Ordinal);
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{text}_{read.State}_x{region.X}y{region.Y}w{windowWidth}h{windowHeight}.png");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Cv2.ImWrite(Path.Combine(folder, name), copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
            }
            finally
            {
                copy.Dispose();
            }
        });
    }

    private void KeepTableCrop(Mat image, Rect region, int windowWidth, int windowHeight, SignalRead single, SignalRead settled)
    {
        if (settled.Value is not { } value || Interlocked.Increment(ref _tableCropsKept) > _mining.TableCropsPerRun)
            return;
        var copy = image.Clone();
        var folder = Path.Combine(_services.Paths.LogsFolder, "mining", "table");
        var text = single.Text.Length == 0 ? "none" : single.Text.Replace(",", string.Empty, StringComparison.Ordinal);
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{value}_{DateTime.Now:yyyyMMdd-HHmmss-fff}_{text}_table_x{region.X}y{region.Y}w{windowWidth}h{windowHeight}.png");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Cv2.ImWrite(Path.Combine(folder, name), copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
            }
            finally
            {
                copy.Dispose();
            }
        });
    }

    private void KeepLossGroup(LossGroup group)
    {
        var slot = (group.Kind == LossKind.Mining ? 2 : 0) + (group.Unsure ? 1 : 0);
        var written = Interlocked.Increment(ref _lossGroupsKept[slot]) <= _pipe.LossGroupsPerFolder;
        _stats?.AddLossGroup(group.Kind, group.Unsure, written);
        if (!written)
        {
            group.Dispose();
            return;
        }

        var folder = Path.Combine(_services.Paths.LogsFolder, "loss", group.Kind == LossKind.Mining ? "mining" : "names");
        if (group.Unsure)
            folder = Path.Combine(folder, "unsure");
        var mid = group.MarkerId > 0 ? string.Create(CultureInfo.InvariantCulture, $"_{group.MarkerId}") : string.Empty;
        var stem = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{LossGroup.FileSafe(group.Label)}{mid}");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var shot in group.Shots)
                    Cv2.ImWrite(Path.Combine(folder, stem + "_" + shot.Role + ".png"), shot.Image);
                File.WriteAllText(Path.Combine(folder, stem + ".json"), group.Sidecar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
            }
            finally
            {
                group.Dispose();
            }
        });
    }
}
