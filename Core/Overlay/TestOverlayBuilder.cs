using Iris.Core.Config;
using Iris.Core.Pipeline;

namespace Iris.Core.Overlay;

public static class TestOverlayBuilder
{
    public static readonly IReadOnlyList<string> GenericNames = new[] { "John", "Adam", "Steve", "Mike", "Chris", "David", "Paul", "Mark" };

    public static string OrgName(int number) => "COMPANY" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly (double X, double Y)[] Jitter =
    {
        (-0.10, 0.12), (0.08, -0.10), (-0.04, 0.05), (0.12, 0.10),
        (0.06, -0.12), (-0.12, 0.08), (0.10, -0.04), (-0.06, -0.10),
    };

    private const int Columns = 4;
    private const int Rows = 2;

    public static OverlayModel Build(SocialConfig config, double screenWidth, double screenHeight, TrackingBoundary? boundary = null)
    {
        var area = boundary ?? TrackingBoundary.Default;
        var names = GenericNames.GetEnumerator();
        string NextName() => names.MoveNext() ? names.Current : "Pilot";

        var shown = new SocialConfig { Name = config.Name };
        var made = new Dictionary<string, IReadOnlyList<OrgMembership>>(StringComparer.OrdinalIgnoreCase);
        var handles = new List<string>();

        foreach (var player in config.Players.Take(3))
        {
            var handle = NextName();
            shown.Players.Add(new PlayerEntry(handle, player.Color));
            handles.Add(handle);
        }

        var orgNumber = 0;
        foreach (var org in config.Orgs.Take(2))
        {
            orgNumber++;
            var handle = NextName();
            var sid = OrgName(orgNumber);
            shown.Orgs.Add(new OrgEntry(sid, org.Priority, org.Color));
            made[handle] = new[] { new OrgMembership(sid, true) };
            handles.Add(handle);
        }

        shown.OrgRules.AddRange(config.OrgRules);
        NoMatchRule.Ensure(shown);

        var rule = config.OrgRules.FirstOrDefault(r => r.Kind != AttributeKind.NoMatch);
        if (rule is not null)
        {
            orgNumber++;
            var handle = NextName();
            made[handle] = OrgsFor(rule, OrgName(orgNumber));
            handles.Add(handle);
        }

        handles.Add(NextName());
        handles.Add(NextName());

        var detections = new List<DetectedPlayer>(handles.Count);
        for (var i = 0; i < handles.Count; i++)
            detections.Add(new DetectedPlayer(handles[i], BoxFor(i, handles[i], screenWidth, screenHeight, area)));

        IReadOnlyList<OrgMembership>? Lookup(string handle) => made.TryGetValue(handle, out var orgs) ? orgs : null;

        var model = OverlayModelBuilder.Build(detections, shown, Lookup);
        return new OverlayModel(model.Brackets, model.Panel, area.ToPixels(screenWidth, screenHeight));
    }

    private static IReadOnlyList<OrgMembership> OrgsFor(OrgAttributeRule rule, string sid) => rule.Kind switch
    {
        AttributeKind.NoOrg => Array.Empty<OrgMembership>(),
        AttributeKind.Redacted => new[] { new OrgMembership(null, true) },
        AttributeKind.Archetype => new[] { new OrgMembership(sid, true, rule.Value) },
        AttributeKind.Activity => new[] { new OrgMembership(sid, true, null, new[] { rule.Value ?? string.Empty }) },
        _ => Array.Empty<OrgMembership>(),
    };

    public static PixelRect BoxFor(int index, string handle, double screenWidth, double screenHeight, TrackingBoundary? boundary = null)
    {
        var area = (boundary ?? TrackingBoundary.Default).ToPixels(screenWidth, screenHeight);
        var regionLeft = area.X;
        var regionRight = area.Right;
        var regionTop = area.Y;
        var regionBottom = area.Bottom;
        var cellWidth = (regionRight - regionLeft) / Columns;
        var cellHeight = (regionBottom - regionTop) / Rows;

        var boxHeight = screenHeight * 0.032;
        var boxWidth = Math.Min(screenHeight * (0.04 + 0.0055 * handle.Length), cellWidth * 0.9);

        var slot = index % (Columns * Rows);
        var column = slot % Columns;
        var row = slot / Columns;
        var (jx, jy) = Jitter[slot];
        var centerX = regionLeft + (column + 0.5 + jx) * cellWidth;
        var centerY = regionTop + (row + 0.5 + jy) * cellHeight;

        var x = Math.Clamp(centerX - boxWidth / 2, regionLeft, Math.Max(regionLeft, regionRight - boxWidth));
        var y = Math.Clamp(centerY - boxHeight / 2, regionTop, Math.Max(regionTop, regionBottom - boxHeight));
        return new PixelRect(x, y, boxWidth, boxHeight);
    }
}
