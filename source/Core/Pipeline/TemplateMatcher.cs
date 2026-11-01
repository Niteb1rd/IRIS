using OpenCvSharp;

namespace Iris.Core.Pipeline;

public readonly record struct TemplateHit(double Score, int X, int Y);

public interface ITemplateMatcher
{
    TemplateHit? Match(Mat grey, Mat template, Rect window);
}

public sealed class TemplateMatcher : ITemplateMatcher, IDisposable
{
    private readonly Mat _result = new();

    public TemplateHit? Match(Mat grey, Mat template, Rect window)
    {
        if (grey.Empty() || template.Empty() || grey.Channels() != 1 || template.Channels() != 1)
            return null;
        if (window.X < 0 || window.Y < 0 || window.X + window.Width > grey.Width || window.Y + window.Height > grey.Height
            || window.Width < template.Width || window.Height < template.Height)
            return null;
        Cv2.MeanStdDev(template, out Scalar _, out Scalar spread);
        if (!(spread.Val0 >= PipelineSettings.TemplateFlatSpread))
            return new TemplateHit(-1.0, window.X, window.Y);
        using var view = new Mat(grey, window);
        Cv2.MatchTemplate(view, template, _result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(_result, out _, out var best, out _, out var at);
        var score = double.IsFinite(best) ? best : -1.0;
        return new TemplateHit(score, window.X + at.X, window.Y + at.Y);
    }

    public void Dispose() => _result.Dispose();
}
