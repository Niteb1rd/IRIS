using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Iris.Core.Lookup;

namespace Iris.Core.Updates;

public sealed record LatestRelease(ReleaseVersion Version, string Tag, string? PortableSha256)
{
    public bool CanInstall => PortableSha256 is not null;

    public Uri DownloadUri => new(string.Format(CultureInfo.InvariantCulture, CacheSettings.UpdateDownloadUrlFormat, Uri.EscapeDataString(Tag)));
}

public sealed record UpdateCheckResult(bool Success, LatestRelease? Release, string? Error)
{
    public ReleaseVersion? Newer => Release?.Version;

    public static UpdateCheckResult UpToDate { get; } = new(true, null, null);

    public static UpdateCheckResult Available(LatestRelease release) => new(true, release, null);

    public static UpdateCheckResult Failed(string error) => new(false, null, error);
}

public sealed class UpdateCheckClient
{
    private readonly HttpClient _http;
    private readonly ILog _log;
    private readonly IClock _clock;

    public UpdateCheckClient(HttpClient http, IClock clock, ILog? log = null)
    {
        _http = http;
        _clock = clock;
        _log = log ?? NullLog.Instance;
    }

    public static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = CacheSettings.UpdateCheckTimeout };

    public async Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CacheSettings.UpdateCheckUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "IRIS/" + current);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code == 404)
                return Fail("no public release yet (HTTP 404)", stopwatch.Elapsed);
            if (code < 200 || code > 299)
                return Fail($"HTTP {code}", stopwatch.Elapsed);
            if (response.Content.Headers.ContentLength is > CacheSettings.UpdateCheckMaxBytes)
                return Fail("answer too large", stopwatch.Elapsed);

            using var body = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    body.Write(chunk, 0, read);
                    if (body.Length > CacheSettings.UpdateCheckMaxBytes)
                        return Fail("answer too large", stopwatch.Elapsed);
                }
            }

            var latest = ParseRelease(body.ToArray());
            if (latest is null)
                return Fail("no version in the answer", stopwatch.Elapsed);

            var newer = latest.Version.IsNewerThan(current);
            Log($"Update check: newest public release {latest.Version}, this build {current}{(newer ? " (newer available)" : string.Empty)}"
                + $"{(newer && !latest.CanInstall ? " without a verifiable " + CacheSettings.UpdateAssetName : string.Empty)} ({RsiClient.FormatDuration(stopwatch.Elapsed)})");
            return newer ? UpdateCheckResult.Available(latest) : UpdateCheckResult.UpToDate;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail($"no answer within {CacheSettings.UpdateCheckTimeout.TotalSeconds:0} seconds", stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return Fail(ex.Message, stopwatch.Elapsed);
        }
        catch (IOException ex)
        {
            return Fail(ex.Message, stopwatch.Elapsed);
        }
    }

    public static LatestRelease? ParseRelease(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
                return null;
            if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
                return null;
            var tag = tagElement.GetString()!;
            var version = ReleaseVersion.TryParse(tag);
            if (version is null || tag.Trim() != tag || tag.Contains('+'))
                return null;
            return new LatestRelease(version, tag, PortableDigest(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? PortableDigest(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !string.Equals(name.GetString(), CacheSettings.UpdateAssetName, StringComparison.Ordinal))
                continue;
            if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String)
                return null;
            const string prefix = "sha256:";
            var text = digest.GetString()!;
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            var hex = text[prefix.Length..].ToLowerInvariant();
            return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? hex : null;
        }
        return null;
    }

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private UpdateCheckResult Fail(string error, TimeSpan elapsed)
    {
        Log($"Update check: {error} ({RsiClient.FormatDuration(elapsed)})");
        return UpdateCheckResult.Failed(error);
    }

    private void Log(string text) => _log.Write(_clock.UtcNow, text);
}
