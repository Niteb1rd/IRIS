using System.Net;
using System.Security.Cryptography;
using System.Text;
using Iris.Core.Updates;
using Iris.Tests.Support;
using Xunit;

namespace Iris.Tests.Updates;

public class UpdateDownloaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "iris-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;

    public UpdateDownloaderTests()
    {
        Directory.CreateDirectory(_folder);
        _target = Path.Combine(_folder, "update.zip");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static LatestRelease Release(string sha) => new(ReleaseVersion.TryParse("v9.9.9")!, "v9.9.9", sha);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private UpdateDownloader Downloader(Stream body, TimeSpan stall)
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        return new UpdateDownloader(new HttpClient(handler), new FakeClock(), null, stall);
    }

    [Fact]
    public async Task ADownloadThatGoesSilentFailsInsteadOfHanging()
    {
        var download = Downloader(new SilentStream(), TimeSpan.FromMilliseconds(50)).DownloadAsync(Release(new string('0', 64)), _target, "test");

        var finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(download, finished);
        Assert.False((await download).Success);
        Assert.False(File.Exists(_target + ".part"));
    }

    [Fact]
    public async Task ADownloadThatKeepsFlowingCompletes()
    {
        var data = Encoding.ASCII.GetBytes("zip bytes");

        var result = await Downloader(new MemoryStream(data), TimeSpan.FromSeconds(30)).DownloadAsync(Release(Sha(data)), _target, "test");

        Assert.True(result.Success);
        Assert.Equal(data, await File.ReadAllBytesAsync(_target));
    }
}
