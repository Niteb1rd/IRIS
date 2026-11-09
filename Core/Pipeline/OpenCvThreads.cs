using OpenCvSharp;

namespace Iris.Core.Pipeline;

public static class OpenCvThreads
{
    public static void Apply() => Cv2.SetNumThreads(LiveSettings.OpenCvThreads);
}
