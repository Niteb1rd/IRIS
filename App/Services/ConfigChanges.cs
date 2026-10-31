using System.Collections.Generic;

namespace Iris.App.Services;

public sealed class ConfigChanges
{
    public ConfigChanges(bool listChanged, bool settingsChanged, bool exclusionsChanged, bool errorsChanged, IReadOnlyList<string> messages)
    {
        ListChanged = listChanged;
        SettingsChanged = settingsChanged;
        ExclusionsChanged = exclusionsChanged;
        ErrorsChanged = errorsChanged;
        Messages = messages;
    }

    public bool ListChanged { get; }

    public bool SettingsChanged { get; }

    public bool ExclusionsChanged { get; }

    public bool ErrorsChanged { get; }

    public IReadOnlyList<string> Messages { get; }

    public bool Any => ListChanged || SettingsChanged || ExclusionsChanged || ErrorsChanged || Messages.Count > 0;

    public string? Message => Messages.Count == 0 ? null : string.Join(" ", Messages);
}
