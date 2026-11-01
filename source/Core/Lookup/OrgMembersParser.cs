using System.Globalization;
using System.Text.Json;
using AngleSharp.Dom;

namespace Iris.Core.Lookup;

public static class OrgMembersParser
{

    private const string SuccessField = "success";
    private const string MessageField = "msg";
    private const string DataField = "data";
    private const string HtmlField = "html";
    private const string TotalField = "totalrows";

    private const string MemberEntrySelector = ".member-item";

    private const string MemberLinkSelector = "a[href*='/citizens/']";

    private const string MemberNickSelector = ".nick";

    private const string HiddenClassMarker = "visibility-R";

    private const string RedactedText = "REDACTED";

    public static RsiResult<OrgMembersPage> ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return RsiResult<OrgMembersPage>.Failed("members response is not a JSON object");

            int? success = root.TryGetProperty(SuccessField, out var successElement) ? ReadInt(successElement) : null;
            if (success != 1)
            {
                var message = root.TryGetProperty(MessageField, out var msg) && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString()
                    : null;
                return RsiResult<OrgMembersPage>.Failed($"members API reported failure ({message ?? "no message"})");
            }

            if (!root.TryGetProperty(DataField, out var data) || data.ValueKind != JsonValueKind.Object)
                return RsiResult<OrgMembersPage>.Failed("members response has no data");
            if (!data.TryGetProperty(HtmlField, out var htmlElement) || htmlElement.ValueKind != JsonValueKind.String)
                return RsiResult<OrgMembersPage>.Failed("members response has no html");

            int? total = data.TryGetProperty(TotalField, out var totalElement) ? ReadInt(totalElement) : null;
            return RsiResult<OrgMembersPage>.Found(ParseFragment(htmlElement.GetString() ?? "", total));
        }
        catch (JsonException ex)
        {
            return RsiResult<OrgMembersPage>.Failed($"members response is not valid JSON: {ex.Message}");
        }
    }

    public static OrgMembersPage ParseFragment(string html, int? totalCount = null)
    {
        var doc = HtmlText.Parse(html);
        var handles = new List<string>();
        int entries = 0;
        foreach (var entry in doc.QuerySelectorAll(MemberEntrySelector))
        {
            entries++;
            var handle = HandleOf(entry);
            if (handle is not null)
                handles.Add(handle);
        }
        return new OrgMembersPage(handles, entries, totalCount);
    }

    private static string? HandleOf(IElement entry)
    {
        if ((entry.ClassName ?? "").Contains(HiddenClassMarker, StringComparison.Ordinal))
            return null;

        var href = entry.QuerySelector(MemberLinkSelector)?.GetAttribute("href");
        if (href is not null)
        {
            var path = href.Split('?', '#')[0].TrimEnd('/');
            var segment = Uri.UnescapeDataString(path.Substring(path.LastIndexOf('/') + 1));
            if (RsiNames.IsValidHandle(segment))
                return segment;
        }

        var nick = HtmlText.Of(entry.QuerySelector(MemberNickSelector));
        if (nick.Length > 0
            && !string.Equals(nick, RedactedText, StringComparison.OrdinalIgnoreCase)
            && RsiNames.IsValidHandle(nick))
            return nick;

        return null;
    }

    private static int? ReadInt(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out var n) ? n : null;
            case JsonValueKind.String:
                return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return null;
        }
    }
}
