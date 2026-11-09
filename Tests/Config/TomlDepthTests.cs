using Iris.Core.Config;
using Xunit;

namespace Iris.Tests.Config;

public class TomlDepthTests
{
    private static string Nested(int depth) => "a = " + new string('[', depth) + new string(']', depth);

    [Fact]
    public void OrdinaryConfigFilesAreFine()
    {
        Assert.False(TomlDepth.TooDeep("[[players]]\nhandle = \"PLAYER1\"\ncolor = [1, [2, 3]]\n"));
        Assert.False(TomlDepth.TooDeep(Nested(TomlDepth.Limit)));
    }

    [Fact]
    public void AHostileListFileIsRejectedInsteadOfCrashing()
    {
        var result = SocialConfigToml.Read(Nested(200_000));

        Assert.False(result.IsValid);
        Assert.Contains(TomlDepth.Error, result.Errors);
    }

    [Fact]
    public void AHostileSettingsFileIsRejectedInsteadOfCrashing()
    {
        var text = Nested(200_000);

        Assert.False(AppSettingsToml.Read(text).IsValid);
        Assert.Empty(AppSettingsToml.MissingKeys(text));
    }

    [Fact]
    public void DeepNestingIsRefused()
    {
        Assert.True(TomlDepth.TooDeep(Nested(TomlDepth.Limit + 1)));
        Assert.True(TomlDepth.TooDeep("a = " + string.Concat(Enumerable.Repeat("{b = ", 40))));
    }

    [Theory]
    [InlineData("a = \"[[[[[[[[[[[[[[[[[[[[\"")]
    [InlineData("a = '[[[[[[[[[[[[[[[[[[[['")]
    [InlineData("# [[[[[[[[[[[[[[[[[[[[\nb = 1")]
    [InlineData("a = \"\"\"\n[[[[[[[[[[[[[[[[[[[[\n\"\"\"")]
    [InlineData("a = '''\n[[[[[[[[[[[[[[[[[[[[\n'''")]
    [InlineData("a = \"\\\"[[[[[[[[[[[[[[[[[[[[\"")]
    public void BracketsInsideTextDoNotCount(string text)
    {
        Assert.False(TomlDepth.TooDeep(text));
    }
}
