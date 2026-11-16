
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
    private CancellationTokenSource? _lookup;

    partial void ShowDossier(RosterEntry entry) => Open(entry.Handle, entry.Identity);

    public void OpenDossierFor(string handle) => Open(handle, null);

    private void Open(string handle, MarkerIdentity? live)
    {
        CancelLookUp();
        OpenAfterLookUp(handle, live);
    }

    private void OpenAfterLookUp(string handle, MarkerIdentity? live)
    {
        if (_window is null)
            return;
        _dossierFetch?.Cancel();
        _dossierHandle = handle;
        _dossierLive = live;
        _dossierLoading = true;
        Show();
        _services.Sounds.Play(UiSound.DossierOpen, _services.Settings.SoundVolumePercent);

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
            if (database.GetPlayerExtra(handle) is not { ProfileRefreshed: not null, EnlistedRead: true })
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
        return new DossierView(shownHandle, extra?.Moniker, record, identity, orgs, _dossierLoading, note,
            extra?.Enlisted, extra?.EnlistedRead ?? false, extra?.LastSeenPlate);
    }

    private void LinkNotes(string handle)
    {
        try
        {
            var record = _services.Database.GetPlayerExtra(handle)?.CitizenRecord;
            var (_, changed) = _services.Notes.Link(handle, record, DateTimeOffset.Now, _services.Settings.DateFormat);
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

    public IReadOnlyList<SearchHit> Search(string text)
    {
        var handles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string handle)
        {
            if (handles.Count < SearchLimit && seen.Add(handle))
                handles.Add(handle);
        }

        var seenAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var cacheReadable = SearchCache(() =>
        {
            foreach (var player in _services.Database.SearchSeenPlayers(text, SearchLimit))
            {
                seenAt[player.Handle] = player.LastSeenPlate;
                Add(player.Handle);
            }
        });
        foreach (var handle in _services.Notes.Handles())
        {
            if (handle.Contains(text, StringComparison.OrdinalIgnoreCase))
                Add(handle);
        }
        if (cacheReadable)
        {
            cacheReadable = SearchCache(() =>
            {
                foreach (var handle in _services.Database.SearchSavedPlayers(text, SearchLimit))
                    Add(handle);
            });
        }

        var now = DateTimeOffset.Now;
        var hits = new List<SearchHit>();
        foreach (var handle in handles)
        {
            DateTimeOffset? when = seenAt.TryGetValue(handle, out var at) ? at : null;
            if (when is null && cacheReadable)
                cacheReadable = SearchCache(() => when = _services.Database.GetPlayerExtra(handle)?.LastSeenPlate);
            hits.Add(new SearchHit(handle, DossierText.SearchTag(when, now, _services.Settings.DateFormat)));
        }
        return hits;
    }

    private bool SearchCache(Action read)
    {
        try
        {
            read();
            return true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Interactive Overlay: search failed: " + ex.Message);
            return false;
        }
    }

    public void LookUp(string handle)
    {
        if (_window is null)
            return;
        handle = handle.Trim().ToUpperInvariant();
        if (!RsiNames.IsValidHandle(handle))
        {
            _window.ShowMessage("IRIS: not a valid RSI handle.");
            return;
        }
        CancelLookUp();
        var lookup = new CancellationTokenSource();
        _lookup = lookup;
        _window.ShowMessage($"IRIS: looking up {handle} on RSI...");
        var window = _window;
        var dispatcher = window.Dispatcher;
        _ = Task.Run(() => _services.Refresher.LookupWithAffiliatesAsync(handle, lookup.Token)).ContinueWith(task => dispatcher.BeginInvoke(new Action(() =>
        {
            if (lookup.IsCancellationRequested || _window is null || !ReferenceEquals(_window, window) || !ReferenceEquals(_lookup, lookup))
                return;
            _lookup = null;
            if (task.IsFaulted)
            {
                _services.Log.Write(_services.Clock.UtcNow, $"Interactive Overlay: look-up of {handle} failed: {task.Exception?.GetBaseException().Message}");
                _window.ShowMessage("IRIS: look-up failed (see the log).");
                return;
            }
            switch (task.IsCompletedSuccessfully ? task.Result : null)
            {
                case { Status: PlayerLookupStatus.Found } found:
                    _window.ShowMessage(string.Empty);
                    OpenAfterLookUp(found.Handle, null);
                    break;
                case { Status: PlayerLookupStatus.NotFound }:
                    _window.ShowMessage($"IRIS: {handle} was not found on RSI.");
                    break;
                default:
                    _window.ShowMessage("IRIS: RSI could not be reached. Try again later.");
                    break;
            }
        })), TaskScheduler.Default);
    }

    public void CloseDossier()
    {
        _dossierFetch?.Cancel();
        _window?.CloseDossierView();
        _dossierHandle = null;
        _dossierLive = null;
    }

    private void CancelLookUp()
    {
        if (_lookup is null)
            return;
        _lookup.Cancel();
        _lookup = null;
        _window?.ShowMessage(string.Empty);
    }

    partial void SaveBeforeExit()
    {
        CancelLookUp();
        CloseDossier();
    }
}
