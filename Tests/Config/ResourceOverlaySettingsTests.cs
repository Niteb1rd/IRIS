using Iris.Core.Config;
using Iris.Core.Overlay;
using Xunit;

namespace Iris.Tests.Config;

public class ResourceOverlaySettingsTests
{
    [Fact]
    public void ANegativeOffsetIsWrittenTheSameInEveryLanguage()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "\u2212";
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;

            var text = AppSettingsToml.Write(AppSettings.Defaults);

            Assert.Contains("resource_overlay_right_px = -1", text);
            Assert.True(AppSettingsToml.Read(text).IsValid);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheReadoutIsOffByDefaultAtItsDefaultPlace()
    {
        Assert.False(AppSettings.Defaults.ShowResourceOverlay);
        Assert.Equal(ResourceOverlayPlacement.Unset, AppSettings.Defaults.ResourceOverlayRightPx);
        Assert.Equal(ResourceOverlayPlacement.Unset, AppSettings.Defaults.ResourceOverlayTopPx);
    }

    [Fact]
    public void ASettingsFileFromAnOlderVersionLeavesItOff()
    {
        var text = string.Join('\n', AppSettingsToml.Write(AppSettings.Defaults).Split('\n').Where(line => !line.Contains("resource_overlay")));

        var read = AppSettingsToml.Read(text);

        Assert.True(read.IsValid);
        Assert.False(read.Settings!.ShowResourceOverlay);
    }

    [Fact]
    public void TheOptionAndPlaceSurviveASaveAndLoad()
    {
        var settings = AppSettings.Defaults with { ShowResourceOverlay = true, ResourceOverlayRightPx = 300, ResourceOverlayTopPx = 40 };

        var read = AppSettingsToml.Read(AppSettingsToml.Write(settings));

        Assert.True(read.IsValid);
        Assert.True(read.Settings!.ShowResourceOverlay);
        Assert.Equal(300, read.Settings.ResourceOverlayRightPx);
        Assert.Equal(40, read.Settings.ResourceOverlayTopPx);
    }

    [Fact]
    public void ASavedSettingsFileNamesTheNewKeys()
    {
        var missing = AppSettingsToml.MissingKeys(AppSettingsToml.Write(AppSettings.Defaults));

        Assert.DoesNotContain("show_resource_overlay", missing);
        Assert.DoesNotContain("resource_overlay_right_px", missing);
        Assert.DoesNotContain("resource_overlay_top_px", missing);
    }

    [Fact]
    public void AnOlderSettingsFileIsReportedAsMissingTheNewKeysSoTheyGetAdded()
    {
        var text = string.Join('\n', AppSettingsToml.Write(AppSettings.Defaults).Split('\n').Where(line => !line.Contains("resource_overlay")));

        Assert.Contains("show_resource_overlay", AppSettingsToml.MissingKeys(text));
    }
}
