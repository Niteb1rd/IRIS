using Iris.Core.Config;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public enum RsiState
{
    Pending,
    Verified,
    NotFound,
}

public sealed record NameAnswer(RsiState State, IReadOnlyList<OrgMembership>? Orgs, bool FromRsi = false);

public interface INameSource
{
    NameAnswer? Stored(string name);

    Task<NameAnswer?> AskRsiAsync(string name, SocialConfig config, CancellationToken ct);

    bool NeedsAffiliateTopUp(string sid);

    Task TopUpAffiliateAsync(string sid, CancellationToken ct);

    NameAnswer? Provisional(string name, SocialConfig config) => null;

    bool WantsBackgroundRefresh(string name) => false;

    Task RefreshInBackgroundAsync(string name, CancellationToken ct) => Task.CompletedTask;

    void SetLiveWaiting(int count)
    {
    }
}
