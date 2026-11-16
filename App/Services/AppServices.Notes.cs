
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Config;

namespace Iris.App.Services;

public sealed partial class AppServices
{
    private readonly SemaphoreSlim _notesSave = new(1, 1);
    private readonly Lock _notesLoad = new();
    private DossierNotes? _notes;
    private volatile bool _notesUnreadable;
    private Task _lastNotesSave = Task.CompletedTask;

    public bool NotesUnreadable => _notesUnreadable;

    public DossierNotes Notes
    {
        get
        {
            lock (_notesLoad)
                return _notes ??= LoadNotes();
        }
    }

    private DossierNotes LoadNotes()
    {
        try
        {
            var loaded = DossierNotes.Load(Paths.DossierNotesFile);
            if (loaded.SkippedLines > 0)
                Log.Write(Clock.UtcNow, $"{DossierNotes.FileName}: {loaded.SkippedLines} line(s) matched no rule and were left out.");
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write(Clock.UtcNow, $"{DossierNotes.FileName} could not be read: {ex.Message}; notes are not saved until it can be read.");
            _notesUnreadable = true;
            return new DossierNotes();
        }
    }

    public Task SaveNotesAsync()
    {
        if (_notesUnreadable)
            return Task.CompletedTask;
        var text = Notes.Serialize();
        var path = Paths.DossierNotesFile;
        return _lastNotesSave = Task.Run(async () =>
        {
            await _notesSave.WaitAsync().ConfigureAwait(false);
            try
            {
                DossierNotes.Save(path, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write(Clock.UtcNow, $"{DossierNotes.FileName} could not be saved: {ex.Message}");
            }
            finally
            {
                _notesSave.Release();
            }
        });
    }

    public void FinishNotes()
    {
        if (_notesFinished)
            return;
        _notesFinished = true;
        WaitForNotesSave(NotesSaveWait);
    }

    private bool _notesFinished;

    private static readonly TimeSpan NotesSaveWait = TimeSpan.FromSeconds(1);

    private void WaitForNotesSave(TimeSpan limit)
    {
        try
        {
            _lastNotesSave.Wait(limit);
        }
        catch (AggregateException)
        {
        }
    }
}
