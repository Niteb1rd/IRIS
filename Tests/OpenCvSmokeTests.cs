using OpenCvSharp;
using Xunit;

namespace Iris.Tests;

public class OpenCvSmokeTests
{
    [Fact]
    public void NativeOpenCvLoadsInTheTestHost()
    {
        using var image = new Mat(32, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(8, 8, 16, 8), Scalar.White, -1);

        Assert.Equal(16 * 8, Cv2.CountNonZero(image));
    }
}
