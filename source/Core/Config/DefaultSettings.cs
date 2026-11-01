using System.Reflection;
using System.Text;

namespace Iris.Core.Config;

public static class DefaultSettings
{
    public const string ResourceName = "Iris.Core.Config.settings.txt";

    private static readonly Lazy<string> LazyText = new(LoadText);
    private static readonly Lazy<AppSettings> LazyValues = new(Parse);

    public static string Text => LazyText.Value;

    public static AppSettings Values => LazyValues.Value;

    private static string LoadText()
    {
        using var stream = typeof(DefaultSettings).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded default settings ({ResourceName}) are missing from {typeof(DefaultSettings).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static AppSettings Parse()
    {
        var text = Text;
        var missing = AppSettingsToml.MissingKeys(text).Where(k => k != "hosted_config_last_check").ToList();
        var read = AppSettingsToml.ReadWith(text, new AppSettings());
        var problems = read.Errors.Concat(read.Warnings).Concat(missing.Select(k => k + ": missing")).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException("The embedded default settings are incomplete or invalid: " + string.Join(" ", problems));
        return read.Settings;
    }
}
