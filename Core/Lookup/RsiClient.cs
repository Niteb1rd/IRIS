using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Iris.Core.Lookup;

public sealed class RsiClient : IRsiClient
{
    public const int MembersPageSize = 32;

    private readonly HttpClient _http;
    private readonly RsiRateLimiter _limiter;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly RsiCircuitBreaker _breaker = new();

    public RsiClient(HttpClient http, RsiRateLimiter limiter, IClock clock, ILog? log = null)
    {
        _http = http;
        _limiter = limiter;
        _clock = clock;
        _log = log ?? NullLog.Instance;
    }

    public static HttpClient CreateHttpClient() => new(CreateHandler()) { Timeout = CacheSettings.RsiRequestTimeout };

    public static HttpClientHandler CreateHandler() =>
        new() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };

    public async Task<RsiResult<PlayerOrgs>> GetPlayerOrgsAsync(string handle, CancellationToken ct = default)
    {
        if (!RsiNames.IsValidHandle(handle))
            return Rejected<PlayerOrgs>("player page");

        var url = RsiUrls.PlayerOrgs(handle);
        var response = await SendAsync(HttpMethod.Get, url, null, CacheSettings.RsiPlayerTimeout, ct).ConfigureAwait(false);
        var result = Interpret(response, body => PlayerOrgsParser.Parse(body, handle));
        Finish(HttpMethod.Get, response, result.Status, result.Reason, null);
        return result;
    }

    public async Task<RsiResult<OrgInfo>> GetOrgInfoAsync(string sid, CancellationToken ct = default)
    {
        if (!RsiNames.IsValidSid(sid))
            return Rejected<OrgInfo>("org page");

        var url = RsiUrls.OrgPage(sid);
        var response = await SendAsync(HttpMethod.Get, url, null, CacheSettings.RsiOrgTimeout, ct).ConfigureAwait(false);
        var result = Interpret(response, body => OrgPageParser.Parse(body, sid));
        Finish(HttpMethod.Get, response, result.Status, result.Reason, null);
        return result;
    }

    public async Task<RsiResult<OrgMembers>> GetOrgMembersAsync(string sid, CancellationToken ct = default)
    {
        if (!RsiNames.IsValidSid(sid))
            return Rejected<OrgMembers>("org members");

        var handles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var total = Stopwatch.StartNew();
        int pagesRead = 0;
        int retries = 0;

        int entriesSoFar = 0;
        for (int page = 1; page <= CacheSettings.RsiMaxMemberPages; page++)
        {
            var body = JsonSerializer.Serialize(new { symbol = sid, search = "", pagesize = MembersPageSize, page });

            Response response;
            RsiResult<OrgMembersPage> result;
            int attempt = 0;
            while (true)
            {
                attempt++;
                response = await SendAsync(HttpMethod.Post, RsiUrls.OrgMembersApi, body, CacheSettings.RsiMembersTimeout, ct).ConfigureAwait(false);

                if (response.StatusCode == 404)
                    result = RsiResult<OrgMembersPage>.Failed("members API returned HTTP 404");
                else
                    result = Interpret<OrgMembersPage>(response, OrgMembersParser.ParseResponse);
                NoteAnswer(response);
                NoteBreaker(response, result.Status);

                if (result.Status == RsiStatus.Found || attempt >= 2 || response.Paused)
                    break;
                retries++;
            }

            if (result.Status != RsiStatus.Found)
            {
                if (!response.Paused)
                    LogOutcome(HttpMethod.Post, response.Url, result.Status, result.Reason, $"{sid} page {page}" + (attempt > 1 ? " after a retry" : ""), total.Elapsed);
                return RsiResult<OrgMembers>.Failed(result.Reason ?? "members request failed");
            }

            pagesRead++;
            var members = result.Data!;
            if (members.EntryCount == 0)
            {
                if (page == 1 && members.TotalCount is > 0)
                {
                    var layoutProblem =
                        $"members page 1 had no entries although RSI reports {members.TotalCount} members (page layout may have changed)";
                    LogOutcome(HttpMethod.Post, response.Url, RsiStatus.Failed, layoutProblem, $"{sid} page 1", total.Elapsed);
                    return RsiResult<OrgMembers>.Failed(layoutProblem);
                }
                LogMembers(response.Url, sid, handles.Count, pagesRead, retries, total.Elapsed);
                return RsiResult<OrgMembers>.Found(new OrgMembers(sid, handles));
            }

            foreach (var h in members.Handles)
                if (seen.Add(h))
                    handles.Add(h);

            entriesSoFar += members.EntryCount;
            bool shortPage = members.EntryCount < MembersPageSize;
            bool totalReached = members.TotalCount is not int totalCount || entriesSoFar >= totalCount;
            if (shortPage && totalReached)
            {
                LogMembers(response.Url, sid, handles.Count, pagesRead, retries, total.Elapsed);
                return RsiResult<OrgMembers>.Found(new OrgMembers(sid, handles));
            }
        }

        var gaveUp = $"gave up after {CacheSettings.RsiMaxMemberPages} member pages";
        LogOutcome(HttpMethod.Post, RsiUrls.OrgMembersApi, RsiStatus.Failed, gaveUp, sid, total.Elapsed);
        return RsiResult<OrgMembers>.Failed(gaveUp);
    }

    private void LogMembers(string url, string sid, int members, int pages, int retries, TimeSpan elapsed) =>
        LogOutcome(HttpMethod.Post, url, RsiStatus.Found, null,
            $"{sid}: {members} members, {pages} page{(pages == 1 ? "" : "s")}" + (retries > 0 ? $", {retries} retr{(retries == 1 ? "y" : "ies")}" : ""), elapsed);

    private void Finish(HttpMethod method, Response response, RsiStatus status, string? reason, string? note)
    {
        if (!response.Paused)
            LogOutcome(method, response.Url, status, reason, note, response.Elapsed);
        NoteAnswer(response);
        NoteBreaker(response, status);
    }

    private sealed record Response(
        string Url, int? StatusCode, string? Body, string? Error,
        TimeSpan Elapsed = default, TimeSpan? RetryAfter = null, bool NetworkProblem = false, long? Turn = null, bool Paused = false);

    private async Task<Response> SendAsync(HttpMethod method, string url, string? jsonBody, TimeSpan requestTimeout, CancellationToken ct)
    {
        if (_breaker.IsOpen(_clock.UtcNow, out var remaining, out var justClosed))
            return new Response(url, null, null, $"paused: too many unreadable or forbidden answers, RSI requests resume in {Math.Ceiling(remaining.TotalSeconds):0} s", Paused: true);
        if (justClosed)
            Log($"RSI requests resume: the {CacheSettings.RsiBreakerPause.TotalMinutes:0} minute pause is over");

        var current = url;
        for (int hop = 0; hop <= CacheSettings.RsiMaxRedirects; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) || !RsiUrls.IsAllowed(uri))
                return new Response(current, null, null, "blocked: only https://robertsspaceindustries.com may be contacted");

            var turn = await _limiter.WaitForTurnAsync(ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(requestTimeout);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", CacheSettings.BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", "en");
                if (jsonBody is not null)
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using var httpResponse = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (httpResponse.RequestMessage?.RequestUri is { } final && !RsiUrls.IsAllowed(final))
                    return new Response(final.AbsoluteUri, null, null, "blocked: only https://robertsspaceindustries.com may be contacted", stopwatch.Elapsed);
                int code = (int)httpResponse.StatusCode;

                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var location = httpResponse.Headers.Location;
                    if (method != HttpMethod.Get || location is null)
                        return new Response(uri.AbsoluteUri, code, null, $"unexpected redirect (HTTP {code})");

                    var next = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(uri, location.OriginalString);
                    Log($"{method} {uri.AbsoluteUri} -> redirect (HTTP {code}) to {next.AbsoluteUri} ({FormatDuration(stopwatch.Elapsed)})");
                    current = next.AbsoluteUri;
                    continue;
                }

                var retryAfter = code is 429 or 503 or 403 ? ReadRetryAfter(httpResponse.Headers.RetryAfter) : null;
                string? text = null;
                if (code >= 200 && code <= 299)
                    text = await httpResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return new Response(uri.AbsoluteUri, code, text, null, stopwatch.Elapsed, retryAfter, Turn: turn);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log($"{method} {uri.AbsoluteUri} -> cancelled ({FormatDuration(stopwatch.Elapsed)})");
                throw;
            }
            catch (OperationCanceledException)
            {
                return new Response(uri.AbsoluteUri, null, null, $"timeout after {requestTimeout.TotalSeconds:0} s", stopwatch.Elapsed, null, NetworkProblem: true);
            }
            catch (HttpRequestException ex)
            {
                return new Response(uri.AbsoluteUri, null, null, $"network error: {ex.Message}", stopwatch.Elapsed, null, NetworkProblem: true);
            }
            catch (IOException ex)
            {
                return new Response(uri.AbsoluteUri, null, null, $"network error: {ex.Message}", stopwatch.Elapsed, null, NetworkProblem: true);
            }
            catch (Exception ex)
            {
                return new Response(uri.AbsoluteUri, null, null, $"error: {ex.GetType().Name}: {ex.Message}", stopwatch.Elapsed);
            }
        }

        return new Response(current, null, null, $"too many redirects (more than {CacheSettings.RsiMaxRedirects})");
    }

    private static RsiResult<T> Interpret<T>(Response response, Func<string, RsiResult<T>> parse)
    {
        if (response.Error is not null)
            return RsiResult<T>.Failed(response.Error);

        int code = response.StatusCode ?? 0;
        if (code == 404)
            return RsiResult<T>.NotFound();
        if (code < 200 || code > 299)
            return RsiResult<T>.Failed($"HTTP {code}");

        try
        {
            return parse(response.Body ?? "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RsiResult<T>.Failed($"could not read the page: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private RsiResult<T> Rejected<T>(string what)
    {
        Log($"rejected {what} request: invalid name");
        return RsiResult<T>.Failed("invalid name");
    }

    private void LogOutcome(HttpMethod method, string url, RsiStatus status, string? reason, string? note, TimeSpan elapsed)
    {
        var outcome = status switch
        {
            RsiStatus.Found => "found",
            RsiStatus.NotFound => "not found (HTTP 404)",
            _ => $"error: {reason}",
        };
        Log($"{method} {url} -> {outcome}" + (note is null ? "" : $" [{note}]") + $" ({FormatDuration(elapsed)})");
    }

    public static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private void NoteAnswer(Response response)
    {
        BackoffChange change;
        if (response.StatusCode is { } code)
        {
            if (code == 429 || code == 403 || code >= 500)
                change = _limiter.ReportThrottled(response.RetryAfter);
            else if ((code >= 200 && code <= 299) || code == 404)
                change = _limiter.ReportSuccess(response.Turn);
            else
                return;
        }
        else if (response.NetworkProblem)
        {
            change = _limiter.ReportThrottled();
        }
        else
        {
            return;
        }

        var gap = FormatSeconds(change.After);
        if (change.NextDelay > change.After)
            Log($"RSI is slowing us down: asked us to wait {FormatSeconds(change.NextDelay)} before the next request, then {gap} between requests");
        else if (change.After > change.Before)
            Log($"RSI is slowing us down: waiting {gap} between requests");
        else if (change.After < change.Before)
            Log($"RSI is answering normally again: waiting {gap} between requests");
    }

    private void NoteBreaker(Response response, RsiStatus status)
    {
        if (response.Paused)
            return;
        if (status is RsiStatus.Found or RsiStatus.NotFound)
        {
            _breaker.RecordSuccess();
            return;
        }

        bool forbidden = response.StatusCode == 403;
        bool unreadable = response.StatusCode is >= 200 and <= 299;
        if ((forbidden || unreadable) && _breaker.RecordFailure(_clock.UtcNow))
            Log($"RSI sent {CacheSettings.RsiBreakerThreshold} unreadable or forbidden answers in a row: pausing all RSI requests for {CacheSettings.RsiBreakerPause.TotalMinutes:0} minutes");
    }

    private static string FormatSeconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";

    private TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null)
            return null;
        TimeSpan wait;
        if (header.Delta is { } delta)
            wait = delta;
        else if (header.Date is { } date)
            wait = date - _clock.UtcNow;
        else
            return null;
        return wait > TimeSpan.Zero ? wait : null;
    }

    private void Log(string message) => _log.Write(_clock.UtcNow, message);
}
