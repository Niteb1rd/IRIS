using Iris.Core.Config;

namespace Iris.Core.Overlay;

public static class OverlayModelBuilder
{
    public static OverlayModel Build(
        IReadOnlyList<DetectedPlayer> detections,
        SocialConfig config,
        Func<string, IReadOnlyList<OrgMembership>?> lookupOrgs)
    {
        var brackets = new List<BracketItem>(detections.Count);
        var entries = new List<PanelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var detected in detections)
        {
            var orgs = lookupOrgs(detected.Handle);
            var match = orgs is null
                ? ColorResolver.ResolveUnknown(detected.Handle, config)
                : ColorResolver.Resolve(detected.Handle, orgs, config);

            brackets.Add(new BracketItem(detected.Box, match.Kind, match.Color));

            if (seen.Add(detected.Handle))
            {
                var line = orgs is null ? PanelText.NotLookedUp : PanelText.OrgLine(orgs, match);
                entries.Add(new PanelEntry(detected.Handle, match.Kind, match.Color, line));
            }
        }

        var ordered = entries.OrderBy(e => (int)e.Kind).ToList();
        return new OverlayModel(brackets, ordered);
    }
}
