
using System.Diagnostics;
using System.Globalization;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed partial class ScanWorker
{
    private void RunCheck(MarkerRecord marker, FrameRegion region, double frameMs, int epoch, IndexSnapshot snapshot, List<PixelRect> claimed, List<IndexUpdate> updates)
    {
        _greyOf = null;
        var fresh = marker.LineMs >= frameMs;
        var (ppx, ppy, predictedMargin, _) = marker.Predict(frameMs, _settings);
        var px = fresh ? marker.X : ppx;
        var py = fresh ? marker.Y : ppy;
        var margin = fresh
            ? Math.Max(_settings.SearchBaseLines, _settings.CheckMarginLines) * marker.Height
            : Math.Max(predictedMargin, _settings.CheckMarginLines * marker.Height);
        var crop = new CropImage(region.Image, region.Region, false);
        if (marker.Status == PlateStatus.Confirmed)
        {
            if (_keep.TryGetValue(marker.Id, out var keep) && keep.Following)
            {
                px = keep.CentreX - marker.Width / 2.0;
                py = keep.Top;
                margin = Math.Max(_settings.SearchBaseLines, _settings.CheckMarginLines) * marker.Height;
            }


            CheckConfirmed(marker, crop, px, py, margin, frameMs, epoch, snapshot, updates);
            return;
        }

        var found = Ops.Check(new CheckItem(marker.Id, crop, marker.Line, px, py, margin, marker.Height));
        if (found is { } line && claimed.Any(c => BoxMath.Overlap(line, c) > _settings.ClaimedOverlapTemplate))
            found = null;
        if (found is { } kept)
            claimed.Add(kept);

        if (found is { } line2)
        {
            _fails[marker.Id] = 0;
            if (frameMs - marker.LastFoundMs > IndexModel.FreshnessLimitMs(snapshot, _settings))
                updates.Add(new PositionUpdate(PipelineGroup.Scan, frameMs, epoch, marker.Id, line2));
            return;
        }

        var fails = _fails.GetValueOrDefault(marker.Id) + 1;
        _fails[marker.Id] = fails;
        if (fails >= _settings.UnconfirmedCheckFails)
        {
            End(marker.Id, frameMs, epoch, updates);
            _stats?.AddEndUnconfirmed();
        }
    }

    private void CheckConfirmed(
        MarkerRecord marker, CropImage crop, double px, double py, double margin, double frameMs, int epoch, IndexSnapshot snapshot, List<IndexUpdate> updates)
    {
        var expected = new PixelRect(px, py, marker.Width, marker.Height);
        var near = Ops.ReadNear(crop, expected, margin);
        var distanceRead = near.Ok;
        var tracked = frameMs - marker.LastFoundMs <= IndexModel.FreshnessLimitMs(snapshot, _settings);
        PixelRect? read = distanceRead ? near.Line : null;
        var reanchor = !tracked && read is not null;
        var verified = true;
        PixelRect? anchor = null;
        if (reanchor && read is { } found)
        {
            var byTemplate = Matcher is not null && marker.NameRel is not null && HasTemplate(marker.Id);
            var line = new PixelRect(found.X, found.Y, marker.Width, marker.Height);
            if (!ReanchorTakesAnotherPlate(marker, line, byTemplate, frameMs, snapshot))
                anchor = line;
        }

        var kept = false;
        if (Matcher is not null && marker.NameRel is { } rel)
        {
            if (tracked)
            {
                if (_keep.TryGetValue(marker.Id, out var following))
                    following.Following = false;
            }

            var followed = !tracked && _keep.TryGetValue(marker.Id, out var follow) && follow.Following;
            if (distanceRead && followed)
            {
                kept = TryKeepAlive(marker, rel, crop, px, py, frameMs, tracked, snapshot);
                verified = kept && (anchor is not { } readLine || NameMatchesAt(marker, rel, crop, readLine.X + marker.Width / 2.0, readLine.Y, frameMs, snapshot));
                if (kept && _keep.TryGetValue(marker.Id, out var moved))
                    RefreshTemplate(marker, rel, crop, moved.CentreX - marker.Width / 2.0, moved.Top);
            }
            else if (distanceRead)
            {
                if (reanchor)
                {
                    if (anchor is { } at && NameMatchesAt(marker, rel, crop, at.X + marker.Width / 2.0, at.Y, frameMs, snapshot))
                        RefreshTemplate(marker, rel, crop, at.X, at.Y);
                    else
                        verified = false;
                }
                else
                {
                    RefreshTemplate(marker, rel, crop, px, py);
                }
            }
            else
            {
                kept = TryKeepAlive(marker, rel, crop, px, py, frameMs, tracked, snapshot);
            }
        }

        if (verified && anchor is { } target)
            updates.Add(new PositionUpdate(PipelineGroup.Scan, frameMs, epoch, marker.Id, target, PartialLine.StoredWidth(marker)));

        var fails = distanceRead || kept ? 0 : _fails.GetValueOrDefault(marker.Id) + 1;
        var lost = tracked || kept ? 0 : _lost.GetValueOrDefault(marker.Id) + 1;
        _fails[marker.Id] = fails;
        _lost[marker.Id] = lost;
        if (fails >= _settings.ConfirmedCheckFails || lost >= _settings.ConfirmedNoTrackChecks)
        {
            End(marker.Id, frameMs, epoch, updates);
            if (fails >= _settings.ConfirmedCheckFails)
                _stats?.AddEndConfirmedReads();
            else
                _stats?.AddEndConfirmedNoTracking();
        }
    }

    private static PixelRect NameBoxAt(PixelRect rel, double centreX, double top) => new(centreX + rel.X, top + rel.Y, rel.Width, rel.Height);
}
