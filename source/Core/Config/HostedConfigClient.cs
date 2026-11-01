using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Iris.Core.Lookup;

namespace Iris.Core.Config;

public sealed class HostedConfigResult
{
    private HostedConfigResult()
    {
    }

    public bool Success { get; private init; }

    public string? Error { get; private init; }

    public string Url { get; private init; } = string.Empty;

    public string? Text { get; private init; }

    public string? Hash { get; private init; }

    internal static HostedConfigResult Ok(string url, string text, string hash) =>
        new() { Success = true, Url = url, Text = text, Hash = hash };

    internal static HostedConfigResult Failed(string url, string error) =>
        new() { Success = false, Url = url, Error = error };
}

public sealed class HostedConfigClient
{
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly ILog _log;

    public bool LogRequests { get; init; } = true;

    public HostedConfigClient(HttpClient http, IClock clock, ILog? log = null)
    {
        _http = http;
        _clock = clock;
        _log = log ?? NullLog.Instance;
    }

    public static HttpClient CreateHttpClient() => new(CreateHandler()) { Timeout = CacheSettings.HostedRequestTimeout };

    public static HttpClientHandler CreateHandler() =>
        new() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };

    public async Task<HostedConfigResult> FetchAsync(string url, CancellationToken ct = default)
    {
        var problem = HostedConfigUrl.Check(url);
        if (problem is not null)
        {
            Log($"hosted config: address refused ({problem}): {Display(url ?? string.Empty)}");
            return HostedConfigResult.Failed(url ?? string.Empty, problem);
        }

        var uri = new Uri(url.Trim());
        var startHost = uri.Host;
        var startPort = uri.Port;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CacheSettings.HostedRequestTimeout);

        for (int hop = 0; hop <= CacheSettings.HostedMaxRedirects; hop++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", Iris.Core.Lookup.CacheSettings.BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/toml, text/plain, */*");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                int code = (int)response.StatusCode;

                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return Fail(uri, "The URL redirects without saying where to.", stopwatch.Elapsed);

                    var next = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(uri, location.OriginalString);
                    LogRequest($"GET {Display(uri)} -> redirect (HTTP {code}) to {Display(next)} ({RsiClient.FormatDuration(stopwatch.Elapsed)})");
                    if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        return Fail(uri, "The URL redirects to an address that is not https; only https:// is allowed.", stopwatch.Elapsed);
                    if (!string.Equals(next.Host, startHost, StringComparison.OrdinalIgnoreCase) || next.Port != startPort)
                        return Fail(uri, $"The URL redirects to another site ({next.Authority}); use the final address instead.", stopwatch.Elapsed);
                    uri = next;
                    continue;
                }

                if (code == 404)
                    return Fail(uri, "The URL returned HTTP 404 (not found). Check the address.", stopwatch.Elapsed);
                if (code < 200 || code > 299)
                    return Fail(uri, $"The server answered HTTP {code}.", stopwatch.Elapsed);

                var announced = response.Content.Headers.ContentLength;
                if (announced is > CacheSettings.ConfigMaxFileBytes)
                    return Fail(uri, "The file is too large (over 1 MB). A config file is normally a few KB.", stopwatch.Elapsed);

                using var body = new MemoryStream();
                await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                {
                    var chunk = new byte[16 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        body.Write(chunk, 0, read);
                        if (body.Length > CacheSettings.ConfigMaxFileBytes)
                            return Fail(uri, "The file is too large (over 1 MB). A config file is normally a few KB.", stopwatch.Elapsed);
                    }
                }

                var bytes = body.ToArray();
                var hash = Sha256Hex(bytes);
                LogRequest($"GET {Display(uri)} -> ok (HTTP {code}, {bytes.Length} bytes, sha256 {hash.Substring(0, 12)}...) ({RsiClient.FormatDuration(stopwatch.Elapsed)})");
                return HostedConfigResult.Ok(Display(uri), ConfigImporter.DecodeUtf8(bytes), hash);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LogRequest($"GET {Display(uri)} -> cancelled ({RsiClient.FormatDuration(stopwatch.Elapsed)})");
                throw;
            }
            catch (OperationCanceledException)
            {
                return Fail(uri, $"The server did not answer within {CacheSettings.HostedRequestTimeout.TotalSeconds:0} seconds.", stopwatch.Elapsed);
            }
            catch (HttpRequestException ex)
            {
                return Fail(uri, "Network error: " + ex.Message, stopwatch.Elapsed);
            }
            catch (IOException ex)
            {
                return Fail(uri, "Network error: " + ex.Message, stopwatch.Elapsed);
            }
            catch (UriFormatException)
            {
                return Fail(uri, "The URL redirects to an address that is not valid.", stopwatch.Elapsed);
            }
        }

        return Fail(uri, $"The URL redirects too many times (more than {CacheSettings.HostedMaxRedirects}).");
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private HostedConfigResult Fail(Uri uri, string error, TimeSpan? elapsed = null)
    {
        Log($"GET {Display(uri)} -> error: {error}" + (elapsed is { } took ? $" ({RsiClient.FormatDuration(took)})" : string.Empty));
        return HostedConfigResult.Failed(Display(uri), error);
    }

    private static string Display(string text)
    {
        try
        {
            if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
                return Display(parsed);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
        }
        return text;
    }

    private static string Display(Uri uri)
    {
        if (uri.UserInfo.Length == 0)
            return uri.AbsoluteUri;
        try
        {
            var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            return builder.Uri.AbsoluteUri;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
            return uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", string.Empty) + uri.PathAndQuery;
        }
    }

    private void Log(string message) => _log.Write(_clock.UtcNow, message);

    private void LogRequest(string message)
    {
        if (LogRequests)
            Log(message);
    }
}
