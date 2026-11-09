namespace Iris.Core.Pipeline;

public sealed record RosterEntry(string Handle, MarkerIdentity Identity, double LastSeenMs, bool Recent);

public static class InteractiveRoster
{
    public const double WindowMs = 5 * 60 * 1000;

    public static IReadOnlyList<RosterEntry> Build(IndexSnapshot snapshot, double nowMs, double windowMs = WindowMs)
    {
        var newest = new Dictionary<string, RosterEntry>(StringComparer.OrdinalIgnoreCase);

        void Offer(MarkerIdentity? identity, double seenMs)
        {
            if (identity is not { Rsi: RsiState.Verified } || string.IsNullOrEmpty(identity.Handle))
                return;
            var entry = new RosterEntry(identity.Handle, identity, seenMs, nowMs - seenMs <= windowMs);
            if (!newest.TryGetValue(identity.Handle, out var known) || known.LastSeenMs < seenMs)
                newest[identity.Handle] = entry;
        }

        foreach (var marker in snapshot.Markers)
            if (marker.Status == PlateStatus.Confirmed)
                Offer(marker.Identity, marker.LastFoundMs);
        foreach (var ended in snapshot.RecentlyEnded)
            Offer(ended.Identity, ended.LastFoundMs);
        foreach (var remembered in snapshot.SessionMemory)
            Offer(remembered.Identity, remembered.EndedMs);

        return newest.Values.OrderByDescending(e => e.LastSeenMs).ToList();
    }
}
