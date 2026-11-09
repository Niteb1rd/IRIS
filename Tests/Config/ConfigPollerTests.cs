using Iris.Core.Config;
using Iris.Tests.Support;
using Xunit;

namespace Iris.Tests.Config;

public class ConfigPollerTests : IDisposable
{
    private const string Saved = "# saved by IRIS\n";
    private const string Edited = "# edited somewhere else, longer\n";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "iris-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly string _lastGood;
    private readonly WatchedFile _file;

    public ConfigPollerTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "settings.txt");
        _lastGood = Path.Combine(_folder, "settings.last-good.txt");
        _file = new WatchedFile(_path, _lastGood, new FakeClock());
        File.WriteAllText(_path, Saved);
        _file.Wrote(Saved);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AnExternalRevertToTheLastSavedSettingsIsApplied()
    {
        File.WriteAllText(_path, Edited);
        Assert.Equal(PollKind.Applied, ConfigPoller.PollSettings(_file).Kind);

        File.WriteAllText(_path, Saved);

        Assert.Equal(PollKind.Applied, ConfigPoller.PollSettings(_file).Kind);
    }

    [Fact]
    public void AnExternalRevertToTheLastSavedListIsApplied()
    {
        const string header = "format = \"sc-social-overlay\"\nformat_version = 1\n";
        File.WriteAllText(_path, header + Saved);
        _file.Wrote(header + Saved);
        File.WriteAllText(_path, header + Edited);
        Assert.Equal(PollKind.Applied, ConfigPoller.PollSocialList(_file).Kind);

        File.WriteAllText(_path, header + Saved);

        Assert.Equal(PollKind.Applied, ConfigPoller.PollSocialList(_file).Kind);
    }

    [Fact]
    public void AMissingFileReturningUnchangedIsReported()
    {
        var written = File.GetLastWriteTimeUtc(_path);
        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSettings(_file).Kind);
        File.Delete(_path);
        Assert.Equal(PollKind.Missing, ConfigPoller.PollSettings(_file).Kind);

        File.WriteAllText(_path, Saved);
        File.SetLastWriteTimeUtc(_path, written);

        Assert.Equal(PollKind.Restored, ConfigPoller.PollSettings(_file).Kind);
    }

    [Fact]
    public void AReturnedFileThatIsBrieflyLockedIsStillReported()
    {
        var written = File.GetLastWriteTimeUtc(_path);
        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSettings(_file).Kind);
        File.Delete(_path);
        Assert.Equal(PollKind.Missing, ConfigPoller.PollSettings(_file).Kind);
        File.WriteAllText(_path, Saved);
        File.SetLastWriteTimeUtc(_path, written);

        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(PollKind.Nothing, ConfigPoller.PollSettings(_file).Kind);

        Assert.Equal(PollKind.Restored, ConfigPoller.PollSettings(_file).Kind);
    }

    [Fact]
    public void AnOutdatedPollCannotReplaceANewerSave()
    {
        var staleHash = FileChangeDetector.HashOf(Edited);
        const string newer = "# saved again by IRIS, newest\n";
        _file.Wrote(newer);

        Assert.False(_file.TryAcceptExternal(Edited, staleHash));

        Assert.Equal(FileChangeDetector.HashOf(newer), _file.AppliedHash);
        Assert.Equal(newer, File.ReadAllText(_lastGood));
    }
}
