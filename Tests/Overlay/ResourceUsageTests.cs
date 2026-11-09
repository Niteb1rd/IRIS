using Iris.Core.Overlay;
using Xunit;

namespace Iris.Tests.Overlay;

public class ResourceUsageTests
{
    private const int Pid = 1234;

    private static string Engine(int pid, int engine, string type) =>
        $"pid_{pid}_luid_0x00000000_0x0000C0A9_phys_0_eng_{engine}_engtype_{type}";

    [Theory]
    [InlineData(1000, 1000, 8, 12.5)]
    [InlineData(8000, 1000, 8, 100.0)]
    [InlineData(500, 1000, 1, 50.0)]
    public void CpuIsAShareOfTheWholeMachine(int cpuMs, int elapsedMs, int processors, double expected)
    {
        Assert.Equal(expected, ResourceUsage.CpuPercent(TimeSpan.FromMilliseconds(cpuMs), TimeSpan.FromMilliseconds(elapsedMs), processors), 3);
    }

    [Fact]
    public void CpuStaysInRange()
    {
        Assert.Equal(100.0, ResourceUsage.CpuPercent(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(0.0, ResourceUsage.CpuPercent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(0.0, ResourceUsage.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.Equal(0.0, ResourceUsage.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
    }

    [Fact]
    public void GpuCountsOnlyThisProcess()
    {
        var engines = new[] { (Engine(Pid, 0, "3D"), 7.0), (Engine(999, 0, "3D"), 80.0), (Engine(12345, 0, "3D"), 60.0) };

        Assert.Equal(7.0, ResourceUsage.GpuPercent(engines, Pid));
    }

    [Fact]
    public void GpuAddsEnginesOfOneTypeAndReportsTheBusiestType()
    {
        var engines = new[] { (Engine(Pid, 0, "3D"), 4.0), (Engine(Pid, 1, "3D"), 3.0), (Engine(Pid, 2, "Copy"), 5.0) };

        Assert.Equal(7.0, ResourceUsage.GpuPercent(engines, Pid));
    }

    [Fact]
    public void GpuIsZeroWhenTheProcessHasNoEnginesAndStaysInRange()
    {
        Assert.Equal(0.0, ResourceUsage.GpuPercent(new[] { (Engine(999, 0, "3D"), 80.0) }, Pid));
        Assert.Equal(100.0, ResourceUsage.GpuPercent(new[] { (Engine(Pid, 0, "3D"), 90.0), (Engine(Pid, 1, "3D"), 90.0) }, Pid));
    }

    [Fact]
    public void GpuIgnoresNamesItCannotUnderstand()
    {
        Assert.Equal(0.0, ResourceUsage.GpuPercent(new[] { ("_Total", 50.0), ($"pid_{Pid}_luid_only", 50.0) }, Pid));
    }

    [Fact]
    public void TheTextKeepsItsColumnsSoTheReadoutDoesNotJump()
    {
        var small = ResourceUsage.Text(new ResourceReading(3.2, 412L * 1024 * 1024, 1.4));
        var large = ResourceUsage.Text(new ResourceReading(100.0, 1234L * 1024 * 1024, 100.0));

        Assert.Equal("CPU 3%   MEM 412MB  GPU 1%  ", small);
        Assert.Equal("CPU 100% MEM 1234MB GPU 100%", large);
        Assert.Equal(small.Length, large.Length);
    }

    [Fact]
    public void AnUnavailableGpuReadingIsShownAsDashes()
    {
        Assert.Equal("CPU 3%   MEM 412MB  GPU --% ", ResourceUsage.Text(new ResourceReading(3.0, 412L * 1024 * 1024, null)));
    }

    [Fact]
    public void EveryCharacterOfTheTextCanBeDrawn()
    {
        Assert.True(StampFont.CanDraw(ResourceUsage.Text(new ResourceReading(12.0, 1024L * 1024 * 1024, null))));
        Assert.True(StampFont.CanDraw(ResourceUsage.Text(new ResourceReading(12.0, 1024L * 1024 * 1024, 7.0))));
        Assert.False(StampFont.CanDraw("z"));
    }
}
