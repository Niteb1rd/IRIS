using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Iris.Core.Lookup;

namespace Iris.Core.Updates;

public sealed record UpdateDownloadResult(bool Success, string? Error)
{
    public static UpdateDownloadResult Done { get; } = new(true, null);

    public static UpdateDownloadResult Failed(string error) => new(false, error);
}

public sealed class UpdateDownloader
{
    private readonly TimeSpan _stallTimeout;
    private readonly HttpClient _http;
    private readonly ILog _log;
    private readonly IClock _clock;

    public UpdateDownloader(HttpClient http, IClock clock, ILog? log = null, TimeSpan? stallTimeout = null)
    {
        _stallTimeout = stallTimeout ?? CacheSettings.UpdateDownloadStallTimeout;
        _http = http;
        _clock = clock;
        _log = log ?? NullLog.Instance;
    }

    public static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = CacheSettings.UpdateDownloadTimeout };

    public static bool IsAllowed(Uri uri, bool redirect)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return false;
        var host = uri.IdnHost;
        return redirect
            ? host.EndsWith(CacheSettings.UpdateRedirectHostSuffix, StringComparison.OrdinalIgnoreCase) && host.Length > CacheSettings.UpdateRedirectHostSuffix.Length
            : string.Equals(host, CacheSettings.UpdateDownloadHost, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<UpdateDownloadResult> DownloadAsync(LatestRelease release, string targetPath, string userAgent, CancellationToken ct = default)
    {
        if (release.PortableSha256 is null)
            return Fail("the release has no verifiable " + CacheSettings.UpdateAssetName, TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        var partPath = targetPath + ".part";
        try
        {
            var uri = release.DownloadUri;
            if (!IsAllowed(uri, redirect: false))
                return Fail("refused address " + uri.Host, stopwatch.Elapsed);

            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int code = (int)response.StatusCode;

                if (code is >= 300 and <= 399)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return Fail($"HTTP {code} without an address", stopwatch.Elapsed);
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (hop >= CacheSettings.UpdateDownloadMaxRedirects)
                        return Fail("too many redirects", stopwatch.Elapsed);
                    if (!IsAllowed(next, redirect: true))
                        return Fail("refused redirect to " + next.Host, stopwatch.Elapsed);
                    uri = next;
                    continue;
                }

                if (code < 200 || code > 299)
                    return Fail($"HTTP {code}", stopwatch.Elapsed);
                if (response.Content.Headers.ContentLength is > CacheSettings.UpdateDownloadMaxBytes)
                    return Fail("the file is too large", stopwatch.Elapsed);

                string? hash = null;
                long length = 0;
                await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var chunk = new byte[81920];
                    int read;
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(_stallTimeout);
                    while ((read = await stream.ReadAsync(chunk.AsMemory(), stall.Token).ConfigureAwait(false)) > 0)
                    {
                        stall.CancelAfter(_stallTimeout);
                        length += read;
                        if (length > CacheSettings.UpdateDownloadMaxBytes)
                            break;
                        sha.AppendData(chunk, 0, read);
                        await file.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                    if (length <= CacheSettings.UpdateDownloadMaxBytes)
                        hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }

                if (hash is null)
                    return FailAndDelete(partPath, "the file is too large", stopwatch.Elapsed);
                if (hash != release.PortableSha256)
                    return FailAndDelete(partPath, "the file does not match the release's SHA-256", stopwatch.Elapsed);

                File.Move(partPath, targetPath, overwrite: true);
                Log($"Update: downloaded {CacheSettings.UpdateAssetName} {release.Version} ({length / 1024} KB, SHA-256 verified, {RsiClient.FormatDuration(stopwatch.Elapsed)})");
                return UpdateDownloadResult.Done;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(partPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            return FailAndDelete(partPath, "the download server stopped answering", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return FailAndDelete(partPath, ex.Message, stopwatch.Elapsed);
        }
    }

    private UpdateDownloadResult FailAndDelete(string partPath, string error, TimeSpan elapsed)
    {
        TryDelete(partPath);
        return Fail(error, elapsed);
    }

    private UpdateDownloadResult Fail(string error, TimeSpan elapsed)
    {
        Log($"Update: download failed: {error} ({RsiClient.FormatDuration(elapsed)})");
        return UpdateDownloadResult.Failed(error);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Log(string text) => _log.Write(_clock.UtcNow, text);
}
