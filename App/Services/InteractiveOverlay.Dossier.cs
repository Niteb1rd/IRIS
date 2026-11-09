
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Views;
using Iris.Core.Config;
using Iris.Core.Lookup;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Services;

public sealed partial class InteractiveOverlay
{
    public const int SearchLimit = 10;

    private string? _dossierHandle;
    private MarkerIdentity? _dossierLive;
    private bool _dossierLoading;
    private CancellationTokenSource? _dossierFetch;

    partial void ShowDossier(RosterEntry entry) => Open(entry.Handle, entry.Identity);

    public void OpenDossierFor(string handle) => Open(handle, null);

    private void Open(string handle, MarkerIdentity? live)
    {
        if (_window is null)
            return;
        _dossierFetch?.Cancel();
        _dossierHandle = handle;
        _dossierLive = live;
        _dossierLoading = true;
        Show();

        var fetch = new CancellationTokenSource();
        _dossierFetch = fetch;
        var dispatcher = _window.Dispatcher;
        _ = Task.Run(() => FetchAsync(handle, fetch.Token)).ContinueWith(_ => dispatcher.BeginInvoke(new Action(() =>
        {
            if (fetch.IsCancellationRequested || !string.Equals(_dossierHandle, handle, StringComparison.OrdinalIgnoreCase))
                return;
            _dossierLoading = false;
            LinkNotes(handle);
            Show();
            ForgetIdentity(handle);
            _window?.RefreshRow(handle);
        })), TaskScheduler.Default);
    }

    private async Task FetchAsync(string handle, CancellationToken ct)
    {
        var refresher = _services.Refresher;
        var database = _services.Database;
        try
        {
            if (database.GetPlayerExtra(handle)?.ProfileRefreshed is null)
                await refresher.FetchProfileAsync(handle, ct).ConfigureAwait(false);
            if (refresher.TryClaimStaleRefresh(handle))
                await refresher.RefreshStalePlayerAsync(handle, ct).ConfigureAwait(false);
            if (database.GetPlayer(handle) is { } player)
            {
                foreach (var org in player.Orgs.Where(o => !o.IsMain && o.Sid is not null))
                {
                    ct.ThrowIfCancellationRequested();
                    if (refresher.NeedsAffiliateTopUp(org.Sid!))
                        await refresher.TopUpAffiliateAsync(org.Sid!, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException or IOException)
        {
            _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay: dossier data for {handle} could not be fetched: {ex.Message}");
        }
    }

    private void Show()
    {
        if (_window is null || _dossierHandle is not { } handle)
            return;
        _window.ShowDossier(BuildView(handle));
    }

    private DossierView BuildView(string handle)
    {
        var config = _services.CurrentConfig;
        IReadOnlyList<OrgMembership>? known = _dossierLive?.Orgs.Orgs;
        string shownHandle = handle;
        PlayerExtra? extra = null;
        try
        {
            if (_services.Database.GetPlayer(handle) is { } cached)
            {
                known = _services.Refresher.WithCachedAttributes(cached.Orgs);
                shownHandle = cached.Handle;
            }
            extra = _services.Database.GetPlayerExtra(handle);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay: cache read for {handle} failed: {ex.Message}");
        }

        var model = OverlayModelBuilder.Build(new[] { new DetectedPlayer(shownHandle, default) }, config, _ => known);
        var orgs = known ?? Array.Empty<OrgMembership>();
        var bracket = model.Brackets[0];
        var identity = new MarkerIdentity(shownHandle, bracket.Kind, bracket.Color, model.Panel[0].Orgs, RsiState.Verified);
        string? record = extra?.ProfileRefreshed is null ? (_dossierLoading ? null : ProfilePageParser.NoCitizenRecord) : extra.CitizenRecord;
        var note = _services.Notes.Find(shownHandle, record)?.Text ?? string.Empty;
        return new DossierView(shownHandle, extra?.Moniker, record, identity, orgs, _dossierLoading, note);
    }

    private void LinkNotes(string handle)
    {
        try
        {
            var record = _services.Database.GetPlayerExtra(handle)?.CitizenRecord;
            var (_, changed) = _services.Notes.Link(handle, record, DateTimeOffset.Now);
            if (changed)
                _ = _services.SaveNotesAsync();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay: notes of {handle} not linked: {ex.Message}");
        }
    }

    public void RefreshDossier() => Show();

    public string? NoteFor(string handle) => _services.Notes.PanelText(handle);

    public bool SaveNote(string handle, string? citizenRecord, string text)
    {
        if (_services.NotesUnreadable)
        {
            _window?.ShowMessage($"IRIS: {DossierNotes.FileName} could not be read; notes are not saved.");
            return true;
        }
        if (!_services.Notes.Set(handle, citizenRecord, text, _services.Clock.UtcNow))
            return false;
        _ = _services.SaveNotesAsync();
        return true;
    }

    public IReadOnlyList<string> Search(string text)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in _services.Notes.Handles())
        {
            if (found.Count < SearchLimit && handle.Contains(text, StringComparison.OrdinalIgnoreCase) && seen.Add(handle))
                found.Add(handle);
        }
        try
        {
            foreach (var player in _services.Database.SearchSeenPlayers(text, SearchLimit))
            {
                if (found.Count < SearchLimit && seen.Add(player.Handle))
                    found.Add(player.Handle);
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Interactive Overlay: search failed: " + ex.Message);
        }
        return found;
    }

    public void CloseDossier()
    {
        _dossierFetch?.Cancel();
        _window?.CloseDossierView();
        _dossierHandle = null;
        _dossierLive = null;
    }

    partial void SaveBeforeExit() => CloseDossier();
}
