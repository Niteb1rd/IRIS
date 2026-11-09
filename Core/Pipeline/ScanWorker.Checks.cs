
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
        _templateScore = double.NaN;
        _templateWhy = null;
        _templateDistLh = _templateReachLh = double.NaN;
        _templateMoved = false;
        _heldInPlace = false;
        _checkTries = _failOwnName = _failOtherPlate = _failOther = 0;
        var fresh = marker.LineMs >= frameMs;
        var (ppx, ppy, predictedMargin, _) = marker.Predict(frameMs, _predict);
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

        var readStarted = Stopwatch.GetTimestamp();
        var found = Ops.Check(new CheckItem(marker.Id, crop, marker.Line, px, py, margin, marker.Height));
        _checkReadTicks += Stopwatch.GetTimestamp() - readStarted;
        _checkMaskMs += Ops.LastCheckMaskMs;
        CountTries(marker, null, found is not null, frameMs, snapshot);
        if (found is { } line && claimed.Any(c => BoxMath.Overlap(line, c) > _settings.ClaimedOverlapTemplate))
            found = null;
        if (found is { } kept)
            claimed.Add(kept);

        if (found is { } line2)
        {
            _fails[marker.Id] = 0;
            var moves = frameMs - marker.LastFoundMs > IndexModel.FreshnessLimitMs(snapshot, _settings);
            if (moves)
                updates.Add(new PositionUpdate(PipelineGroup.Scan, frameMs, epoch, marker.Id, line2));
            TraceCheck(marker, frameMs, px, py, margin, found, moves ? line2 : null, false, false, true, 0, 0);
            return;
        }

        var fails = _fails.GetValueOrDefault(marker.Id) + 1;
        _fails[marker.Id] = fails;
        TraceCheck(marker, frameMs, px, py, margin, null, null, false, false, true, fails, 0);
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
        var readStarted = Stopwatch.GetTimestamp();
        var near = Ops.ReadNear(crop, expected, margin);
        _checkReadTicks += Stopwatch.GetTimestamp() - readStarted;
        _checkMaskMs += Ops.LastCheckMaskMs;
        CountTries(marker, marker.NameRel is { } ownRel ? NameBoxAt(ownRel, px + marker.Width / 2.0, py) : null, near.Ok, frameMs, snapshot);
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
        var templateStarted = Stopwatch.GetTimestamp();
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

        _checkTemplateTicks += Stopwatch.GetTimestamp() - templateStarted;
        if (verified && anchor is { } target)
        {
            updates.Add(new PositionUpdate(PipelineGroup.Scan, frameMs, epoch, marker.Id, target, PartialLine.StoredWidth(marker)));
        }
        else if (kept && !tracked && _keep.TryGetValue(marker.Id, out var place) && place.Following)
        {
            updates.Add(new TemplateHitUpdate(PipelineGroup.Scan, frameMs, epoch, marker.Id, place.CentreX, place.Top));
            _stats?.AddTemplateMove();
            _templateMoved = true;
        }

        if (tracked || kept)
            _holdSinceMs.Remove(marker.Id);
        var landedElsewhere = reanchor && (anchor is null || !verified) && read is { } landed && BoxMath.Overlap(landed, expected) <= 0.0;
        _heldInPlace = (!distanceRead || landedElsewhere) && !kept && ReadsInPlace(marker, crop, expected, frameMs, tracked, snapshot);
        var fails = distanceRead || kept || _heldInPlace ? 0 : _fails.GetValueOrDefault(marker.Id) + 1;
        var lost = tracked || kept || _heldInPlace ? 0 : _lost.GetValueOrDefault(marker.Id) + 1;
        _fails[marker.Id] = fails;
        _lost[marker.Id] = lost;
        TraceCheck(marker, frameMs, px, py, margin, read, verified ? anchor : null, tracked, kept, verified, fails, lost);
        if (fails >= _settings.ConfirmedCheckFails || lost >= _settings.ConfirmedNoTrackChecks)
        {
            End(marker.Id, frameMs, epoch, updates);
            if (fails >= _settings.ConfirmedCheckFails)
                _stats?.AddEndConfirmedReads();
            else
                _stats?.AddEndConfirmedNoTracking();
        }
    }

    private bool ReadsInPlace(MarkerRecord marker, CropImage crop, PixelRect expected, double frameMs, bool tracked, IndexSnapshot snapshot)
    {
        if (_holdSinceMs.TryGetValue(marker.Id, out var since) && frameMs - since >= _settings.ConfirmedHoldMaxMs)
            return false;
        if (ReanchorTakesAnotherPlate(marker, expected, false, frameMs, snapshot))
            return false;
        var started = Stopwatch.GetTimestamp();
        var read = Ops.ReadDistance(crop, expected);
        _checkReadTicks += Stopwatch.GetTimestamp() - started;
        if (read)
        {
            _stats?.AddInPlaceHold();
            if (!tracked)
                _holdSinceMs.TryAdd(marker.Id, frameMs);
        }

        return read;
    }

    private void CountTries(MarkerRecord marker, PixelRect? ownName, bool found, double frameMs, IndexSnapshot snapshot)
    {
        var tried = Ops.LastCheckTried;
        _checkTries = tried.Count;
        var failed = found ? tried.Count - 1 : tried.Count;
        for (var i = 0; i < failed; i++)
        {
            var box = tried[i];
            if (ownName is { } name && BoxMath.Overlap(box, name) > 0.0)
                _failOwnName++;
            else if (OverlapsOtherPlate(marker.Id, box, frameMs, snapshot))
                _failOtherPlate++;
            else
                _failOther++;
        }

        _stats?.AddCheckTries(_checkTries, _failOwnName, _failOtherPlate, _failOther, found);
    }

    private void TraceCheck(
        MarkerRecord marker, double frameMs, double px, double py, double margin, PixelRect? read, PixelRect? moved, bool tracked, bool kept, bool verified, int fails, int lost)
    {
        if (Trace.Line(_clock.NowMs, "scn", "check") is not { } t)
            return;
        t.Int("seq", _frameSeq).Num("fms", frameMs).Int("mid", marker.Id).Str("st", marker.Status.ToString()).Box("line", marker.Line).Num("lastFound", marker.LastFoundMs)
            .Num("px", px).Num("py", py).Num("margin", margin).Bool("tracked", tracked).Bool("read", read is not null).Bool("kept", kept).Bool("verified", verified)
            .Bool("following", _keep.TryGetValue(marker.Id, out var keep) && keep.Following).Num("tplScore", _templateScore, 3).Str("tplWhy", _templateWhy)
            .Num("tplDistLh", _templateDistLh, 2).Num("tplReachLh", _templateReachLh, 2).Bool("tplMove", _templateMoved).Bool("held", _heldInPlace)
            .Int("tries", _checkTries).Int("failOwnName", _failOwnName).Int("failOtherPlate", _failOtherPlate).Int("failOther", _failOther)
            .Int("fails", fails).Int("noTrack", lost);
        if (read is { } r)
            t.Box("readBox", r);
        if (moved is { } m)
            t.Box("moveTo", m).Num("moveDist", Math.Sqrt((m.X - marker.X) * (m.X - marker.X) + (m.Y - marker.Y) * (m.Y - marker.Y)), 1);
        Trace!.Write(t.Text());
    }

    private static PixelRect NameBoxAt(PixelRect rel, double centreX, double top) => new(centreX + rel.X, top + rel.Y, rel.Width, rel.Height);
}
