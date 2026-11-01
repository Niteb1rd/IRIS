namespace Iris.Core.Detection;

public interface IContrastMaskSink
{
    void AddContrastMask(int maskPixels, int contrastPixels);
}
