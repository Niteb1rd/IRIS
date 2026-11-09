using Iris.Core.Pipeline;
using OpenCvSharp;
using Xunit;

namespace Iris.Tests.Pipeline;

public class OpenCvThreadsTests
{
    [Fact]
    public void OpenCvIsCappedAtTwoThreads()
    {
        Assert.Equal(2, LiveSettings.OpenCvThreads);
    }

    [Fact]
    public void ApplyingTheCapSetsOpenCvsThreadCount()
    {
        var before = Cv2.GetNumThreads();
        try
        {
            OpenCvThreads.Apply();

            Assert.Equal(LiveSettings.OpenCvThreads, Cv2.GetNumThreads());
        }
        finally
        {
            Cv2.SetNumThreads(before);
        }
    }

    [Fact]
    public void TheScanUsesOneHelperBesideItsOwnThread()
    {
        Assert.Equal(1, LiveSettings.ScanHelpers);
    }
}
