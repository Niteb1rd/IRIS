
using System.Diagnostics;
using System.Globalization;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Pipeline;

public sealed partial class ScanWorker
{
    private bool ToGrey(Mat image)
    {
        if (ReferenceEquals(image, _greyOf))
            return true;
        _greyOf = null;
        switch (image.Channels())
        {
            case 4:
                Cv2.CvtColor(image, _grey, ColorConversionCodes.BGRA2GRAY);
                _greyOf = image;
                return true;
            case 3:
                Cv2.CvtColor(image, _grey, ColorConversionCodes.BGR2GRAY);
                _greyOf = image;
                return true;
            case 1:
                image.CopyTo(_grey);
                _greyOf = image;
                return true;
            default:
                return false;
        }
    }

    private void RefreshTemplate(MarkerRecord marker, PixelRect rel, CropImage crop, double px, double py)
    {
        if (crop.Image.Empty() || !ToGrey(crop.Image))
            return;
        var name = NameBoxAt(rel, px + marker.Width / 2.0, py);
        var nameX = (int)Math.Round(name.X - crop.Origin.X);
        var nameY = (int)Math.Round(name.Y - crop.Origin.Y);
        var x0 = Math.Max(0, nameX - _settings.TemplatePad);
        var y0 = Math.Max(0, nameY - _settings.TemplatePad);
        var x1 = Math.Min(_grey.Width, (int)Math.Round(name.Right - crop.Origin.X) + _settings.TemplatePad);
        var y1 = Math.Min(_grey.Height, (int)Math.Round(name.Bottom - crop.Origin.Y) + _settings.TemplatePad);
        if (x1 - x0 < _settings.MinTemplatePx || y1 - y0 < _settings.MinTemplatePx)
        {
            DropKeep(marker.Id);
            return;
        }

        using var cut = new Mat(_grey, new Rect(x0, y0, x1 - x0, y1 - y0));
        Cv2.MeanStdDev(cut, out Scalar _, out Scalar spread);
        if (!(spread.Val0 >= _settings.MinTemplateSpread))
        {
            DropKeep(marker.Id);
            return;
        }

        if (!_keep.TryGetValue(marker.Id, out var keep))
            _keep[marker.Id] = keep = new KeepAlive();
        cut.CopyTo(keep.Template);
        keep.OffsetX = x0 - nameX;
        keep.OffsetY = y0 - nameY;
    }

    private Rect? TemplateWindow(KeepAlive keep, PixelRect rel, CropImage crop, double centreX, double top, int reach)
    {
        var name = NameBoxAt(rel, centreX, top);
        var tx = (int)Math.Round(name.X - crop.Origin.X) + keep.OffsetX;
        var ty = (int)Math.Round(name.Y - crop.Origin.Y) + keep.OffsetY;
        var left = Math.Max(0, tx - reach);
        var upper = Math.Max(0, ty - reach);
        var right = Math.Min(_grey.Width, tx + keep.Template.Width + reach);
        var lower = Math.Min(_grey.Height, ty + keep.Template.Height + reach);
        if (right - left < keep.Template.Width || lower - upper < keep.Template.Height)
            return null;
        return new Rect(left, upper, right - left, lower - upper);
    }

    private bool NameMatchesAt(MarkerRecord marker, PixelRect rel, CropImage crop, double centreX, double top, double frameMs, IndexSnapshot snapshot)
    {
        if (Matcher is not { } matcher || !_keep.TryGetValue(marker.Id, out var keep) || keep.Template.Empty())
            return true;
        if (crop.Image.Empty() || !ToGrey(crop.Image))
            return false;
        var reach = (int)Math.Ceiling(Math.Max(_settings.MinSearchReachPx, marker.Height));
        if (TemplateWindow(keep, rel, crop, centreX, top, reach) is not { } window)
            return false;
        if (matcher.Match(_grey, keep.Template, window) is not { } hit || !(hit.Score >= _settings.TemplateThreshold))
            return false;
        var matched = new PixelRect(crop.Origin.X + hit.X, crop.Origin.Y + hit.Y, keep.Template.Width, keep.Template.Height);
        return !OverlapsOtherPlate(marker.Id, matched, frameMs, snapshot);
    }

    private bool TryKeepAlive(MarkerRecord marker, PixelRect rel, CropImage crop, double px, double py, double frameMs, bool tracked, IndexSnapshot snapshot)
    {
        if (Matcher is not { } matcher || !_keep.TryGetValue(marker.Id, out var keep) || keep.Template.Empty() || crop.Image.Empty() || !ToGrey(crop.Image))
            return false;
        var centreX = keep.Following ? keep.CentreX : px + marker.Width / 2.0;
        var top = keep.Following ? keep.Top : py;
        var tw = keep.Template.Width;
        var th = keep.Template.Height;
        var reach = (int)Math.Ceiling(Math.Max(_settings.MinSearchReachPx, marker.Height));
        if (!tracked)
            reach = Math.Max(reach, LostReach(marker, keep, frameMs));
        if (TemplateWindow(keep, rel, crop, centreX, top, reach) is not { } window)
            return false;

        if (matcher.Match(_grey, keep.Template, window) is not { } hit)
            return false;
        var score = double.IsFinite(hit.Score) ? hit.Score : -1.0;
        if (!(score >= _settings.TemplateThreshold))
        {
            _stats?.AddTemplateReject();
            return false;
        }

        var matched = new PixelRect(crop.Origin.X + hit.X, crop.Origin.Y + hit.Y, tw, th);
        var centre = matched.X - keep.OffsetX - rel.X;
        var lineTop = matched.Y - keep.OffsetY - rel.Y;
        if (OverlapsOtherPlate(marker.Id, matched, frameMs, snapshot)
            || (!tracked && OnOtherMarkersLine(marker, new PixelRect(centre - marker.Width / 2.0, lineTop, marker.Width, marker.Height), true, frameMs, snapshot)))
        {
            _stats?.AddTemplateGuardReject();
            return false;
        }

        keep.Following = !tracked;
        keep.AnchorMs = frameMs;
        keep.CentreX = centre;
        keep.Top = lineTop;
        _stats?.AddTemplateKeepAlive();
        if (_keepAlivesLogged < KeepAliveLogLimit)
        {
            _keepAlivesLogged++;
            _log.Write(_wall.UtcNow, string.Create(CultureInfo.InvariantCulture, $"Template keep-alive: marker {marker.Id}, score {score:0.00} (distance line unreadable, name area matches)"));
        }

        return true;
    }

    private int LostReach(MarkerRecord marker, KeepAlive keep, double frameMs)
    {
        var since = frameMs - Math.Max(Math.Max(marker.LastFoundMs, marker.LineMs), keep.AnchorMs);
        var seconds = Math.Max(0.0, since / 1000.0);
        var reach = Math.Min(_settings.LostTemplateReachPx, _settings.LostTemplateSpeedPxPerSec * seconds) * _scale;
        return (int)Math.Ceiling(reach);
    }

    private (double X, double Y) PlaceOf(MarkerRecord other, double frameMs)
    {
        if (_keep.TryGetValue(other.Id, out var followed) && followed.Following)
            return (followed.CentreX - other.Width / 2.0, followed.Top);
        if (other.LineMs < frameMs)
        {
            var (ppx, ppy, _, _) = other.Predict(frameMs, _settings);
            return (ppx, ppy);
        }

        return (other.X, other.Y);
    }

    private bool OnOtherMarkersLine(MarkerRecord marker, PixelRect line, bool seenApart, double frameMs, IndexSnapshot snapshot)
    {
        foreach (var other in snapshot.Markers)
        {
            if (other.Id == marker.Id || (seenApart && other.BornMs > marker.LastFoundMs))
                continue;
            var (x, y) = PlaceOf(other, frameMs);
            if (BoxMath.Overlap(line, new PixelRect(x, y, other.Width, other.Height)) > _settings.ClaimedOverlap)
                return true;
        }

        return false;
    }

    private bool ReanchorTakesAnotherPlate(MarkerRecord marker, PixelRect line, bool byTemplate, double frameMs, IndexSnapshot snapshot)
    {
        if (OnOtherMarkersLine(marker, line, byTemplate, frameMs, snapshot))
            return true;
        return !byTemplate && marker.NameRel is { } rel && OverlapsOtherPlate(marker.Id, NameBoxAt(rel, line.X + marker.Width / 2.0, line.Y), frameMs, snapshot);
    }

    private bool OverlapsOtherPlate(int id, PixelRect matched, double frameMs, IndexSnapshot snapshot)
    {
        foreach (var other in snapshot.Markers)
        {
            if (other.Id == id)
                continue;
            var (x, y) = PlaceOf(other, frameMs);
            var area = other.Status == PlateStatus.Confirmed && other.NameRel is { } otherRel
                ? NameBoxAt(otherRel, x + other.Width / 2.0, y)
                : new PixelRect(x, y, other.Width, other.Height);
            if (BoxMath.Overlap(matched, area) > _settings.ClaimedOverlapTemplate)
                return true;
        }

        return false;
    }
}
