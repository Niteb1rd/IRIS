using System.Globalization;
using System.Text;

namespace Iris.Core.Config;

public sealed record DossierNote(string Handle, string? CitizenRecord, DateTimeOffset Updated, string Text);

public sealed class DossierNotes
{
    public const string FileName = "dossier-notes.txt";
    public const int MaxNoteChars = 4000;
    public const long DefaultMaxFileBytes = 20L * 1024 * 1024;
    public const int FormatVersion = 1;

    public const int UniverseYearOffset = 930;

    private const string Header = "# IRIS dossier notes";
    private const string HandleTag = "@handle ";
    private const string CitizenTag = "@citizen ";
    private const string UpdatedTag = "@updated ";
    private const char LinePrefix = '>';

    private readonly object _gate = new();
    private readonly List<DossierNote> _notes = new();
    private readonly long _maxFileBytes;

    public DossierNotes(long maxFileBytes = DefaultMaxFileBytes) => _maxFileBytes = maxFileBytes;

    public int SkippedLines { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _notes.Count;
        }
    }

    public static DossierNotes Load(string path, long maxFileBytes = DefaultMaxFileBytes)
    {
        var notes = new DossierNotes(maxFileBytes);
        if (File.Exists(path))
            notes.ReadFrom(File.ReadAllText(path, Encoding.UTF8));
        return notes;
    }

    public void ReadFrom(string text)
    {
        lock (_gate)
        {
            _notes.Clear();
            SkippedLines = 0;
            string? handle = null;
            string? record = null;
            DateTimeOffset updated = DateTimeOffset.MinValue;
            var lines = new List<string>();

            void Finish()
            {
                if (handle is not null && lines.Count > 0)
                    _notes.Add(new DossierNote(handle, record, updated, string.Join("\n", lines)));
                handle = null;
                record = null;
                updated = DateTimeOffset.MinValue;
                lines.Clear();
            }

            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                if (line.StartsWith(HandleTag, StringComparison.Ordinal))
                {
                    Finish();
                    var name = line.Substring(HandleTag.Length).Trim();
                    handle = name.Length > 0 && !name.Contains(' ') ? name : null;
                    if (handle is null)
                        SkippedLines++;
                }
                else if (line.StartsWith(CitizenTag, StringComparison.Ordinal) && handle is not null)
                {
                    var digits = line.Substring(CitizenTag.Length).Trim();
                    record = digits.Length > 0 && digits.All(char.IsAsciiDigit) ? digits : null;
                }
                else if (line.StartsWith(UpdatedTag, StringComparison.Ordinal) && handle is not null)
                {
                    if (DateTimeOffset.TryParse(line.Substring(UpdatedTag.Length).Trim(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                        updated = time;
                }
                else if (line[0] == LinePrefix && handle is not null)
                {
                    lines.Add(line.Length >= 2 && line[1] == ' ' ? line.Substring(2) : line.Substring(1));
                }
                else
                {
                    SkippedLines++;
                }
            }

            Finish();
        }
    }

    public string Serialize()
    {
        lock (_gate)
            return SerializeLocked(_notes);
    }

    private static string SerializeLocked(IEnumerable<DossierNote> notes)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append(' ').Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var note in notes)
        {
            if (note.Text.Length == 0)
                continue;
            sb.Append("\r\n").Append(HandleTag).Append(note.Handle).Append("\r\n");
            if (note.CitizenRecord is { Length: > 0 } record)
                sb.Append(CitizenTag).Append(record).Append("\r\n");
            sb.Append(UpdatedTag).Append(note.Updated.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append("\r\n");
            foreach (var line in note.Text.Split('\n'))
                sb.Append(LinePrefix).Append(line.Length > 0 ? " " + line : string.Empty).Append("\r\n");
        }
        return sb.ToString();
    }

    public static void Save(string path, string text) => AtomicFile.WriteText(path, text, keepBackup: true);

    public DossierNote? Find(string handle, string? citizenRecord)
    {
        lock (_gate)
        {
            int index = IndexOf(handle, citizenRecord);
            return index >= 0 ? _notes[index] : null;
        }
    }

    public bool HasNote(string handle)
    {
        lock (_gate)
            return _notes.Any(n => string.Equals(n.Handle, handle, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> Handles()
    {
        lock (_gate)
            return _notes.Select(n => n.Handle).ToList();
    }

    public string? PanelText(string handle)
    {
        lock (_gate)
        {
            var note = _notes.FirstOrDefault(n => string.Equals(n.Handle, handle, StringComparison.OrdinalIgnoreCase));
            return note is null ? null : note.Text.Replace('\n', ' ');
        }
    }

    public (DossierNote? Note, bool Changed) Link(string handle, string? citizenRecord, DateTimeOffset localNow)
    {
        lock (_gate)
        {
            var record = IsRecord(citizenRecord) ? citizenRecord : null;
            int index = IndexOf(handle, record);
            if (index < 0)
                return (null, false);
            var note = _notes[index];
            if (record is not null && !string.Equals(note.Handle, handle, StringComparison.OrdinalIgnoreCase))
            {
                var line = $"Handle name changed from {note.Handle} to {handle} seen on {UniverseTime(localNow)}.";
                note = note with { Handle = handle, Text = note.Text + "\n" + line, Updated = localNow.ToUniversalTime() };
                _notes[index] = note;
                return (note, true);
            }

            if (record is not null && note.CitizenRecord is null)
            {
                note = note with { CitizenRecord = record };
                _notes[index] = note;
                return (note, true);
            }

            return (note, false);
        }
    }

    public bool Set(string handle, string? citizenRecord, string text, DateTimeOffset utcNow)
    {
        var clean = Clean(text);
        if (clean.Length > MaxNoteChars)
            clean = clean.Substring(0, MaxNoteChars);
        lock (_gate)
        {
            var record = IsRecord(citizenRecord) ? citizenRecord : null;
            int index = IndexOf(handle, record);
            if (clean.Length == 0)
            {
                if (index >= 0)
                    _notes.RemoveAt(index);
                return true;
            }

            var candidate = new List<DossierNote>(_notes);

            var note = new DossierNote(handle, record ?? (index >= 0 ? _notes[index].CitizenRecord : null), utcNow, clean);
            if (index >= 0)
                candidate[index] = note;
            else
                candidate.Add(note);
            if (Encoding.UTF8.GetByteCount(SerializeLocked(candidate)) > _maxFileBytes)
                return false;
            _notes.Clear();
            _notes.AddRange(candidate);
            return true;
        }
    }

    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var c in normalised)
        {
            if (c == '\n')
                sb.Append(c);
            else if (c == '\t')
                sb.Append("    ");
            else if (!char.IsControl(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static string UniverseTime(DateTimeOffset localNow) =>
        localNow.AddYears(UniverseYearOffset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static bool IsRecord(string? text) => text is { Length: > 0 } && text.All(char.IsAsciiDigit);

    private int IndexOf(string handle, string? record)
    {
        if (IsRecord(record))
        {
            int byRecord = _notes.FindIndex(n => n.CitizenRecord == record);
            if (byRecord >= 0)
                return byRecord;
        }
        return _notes.FindIndex(n => string.Equals(n.Handle, handle, StringComparison.OrdinalIgnoreCase)
                                     && (n.CitizenRecord is null || !IsRecord(record) || n.CitizenRecord == record));
    }
}
