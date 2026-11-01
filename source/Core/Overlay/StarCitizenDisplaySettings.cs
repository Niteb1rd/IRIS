using System.Globalization;
using System.Xml;

namespace Iris.Core.Overlay;

public enum WindowMode
{
    Windowed = 0,
    Borderless = 1,
    Fullscreen = 2,
}

public static class StarCitizenDisplaySettings
{
    public const string BinFolderName = "Bin64";

    public const string AttributeName = "WindowMode";

    private const string ClientFolder = "client";
    private const string PreferredProfileFolder = "0";

    public static string? AttributesPathFor(
        string exePath,
        Func<string, bool> fileExists,
        Func<string, IEnumerable<(string path, DateTime modifiedUtc)>> findCandidates)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return null;

        var binDirectory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(binDirectory) || !string.Equals(Path.GetFileName(binDirectory), BinFolderName, StringComparison.OrdinalIgnoreCase))
            return null;
        var install = Path.GetDirectoryName(binDirectory);
        if (string.IsNullOrEmpty(install))
            return null;

        var clientDirectory = Path.Combine(install, "user", ClientFolder);
        var preferred = Path.Combine(clientDirectory, PreferredProfileFolder, "Profiles", "default", "attributes.xml");
        if (fileExists(preferred))
            return preferred;

        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var (path, modifiedUtc) in findCandidates(clientDirectory))
        {
            if (best is null || modifiedUtc > bestTime)
            {
                best = path;
                bestTime = modifiedUtc;
            }
        }
        return best;
    }

    public static WindowMode? ParseWindowMode(string? xmlText)
    {
        if (string.IsNullOrWhiteSpace(xmlText))
            return null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };

        try
        {
            using var text = new StringReader(xmlText);
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Attr")
                    continue;
                if (reader.GetAttribute("name") != AttributeName)
                    continue;

                var value = reader.GetAttribute("value");
                if (value is null || !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    return null;
                return number switch
                {
                    0 => WindowMode.Windowed,
                    1 => WindowMode.Borderless,
                    2 => WindowMode.Fullscreen,
                    _ => null,
                };
            }
            return WindowMode.Windowed;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
