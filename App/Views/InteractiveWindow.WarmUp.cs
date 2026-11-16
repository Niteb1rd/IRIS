
using System;
using System.Collections.Generic;
using System.Windows;
using Iris.Core.Config;
using Iris.Core.Overlay;
using Iris.Core.Pipeline;

namespace Iris.App.Views;

public sealed partial class InteractiveWindow
{
    public static void WarmUp(AppSettings settings, SocialConfig config, PixelRect bounds)
    {
        var orgs = new PanelOrgs(new[] { new OrgMembership("IRISWARM", true, "Organization", new[] { "Exploration" }, "Warm Up") }, null);
        var identity = new MarkerIdentity("WARM_UP_A", MatchKind.NoMatch, OverlayColor.Grey, orgs, RsiState.Verified);
        var roster = new[]
        {
            new RosterEntry("WARM_UP_A", identity, 0, Recent: true),
            new RosterEntry("WARM_UP_B", identity with { Handle = "WARM_UP_B" }, 0, Recent: false),
        };
        var window = new InteractiveWindow(new WarmUpHost(settings, config), bounds, roster);
        try
        {
            window.BuildContent();
            window.ShowDossier(new DossierView("WARM_UP_A", "Warm Up", "n/a", identity, orgs.Orgs!, Loading: false, NoteText: string.Empty));
            var size = new Size(window._root.Width, window._root.Height);
            window._stage.Measure(size);
            window._stage.Arrange(new Rect(size));
            window._stage.UpdateLayout();
        }
        finally
        {
            window._closing = true;
            window.Close();
        }
    }

    private sealed class WarmUpHost(AppSettings settings, SocialConfig config) : IInteractiveHost
    {
        public AppSettings Settings => settings;

        public SocialConfig Config => config;

        public bool IsOpening => false;

        public bool Edit(Func<SocialConfig, SocialConfig> change) => false;

        public MarkerIdentity IdentityOf(RosterEntry entry) => entry.Identity;

        public void OpenDossier(RosterEntry entry) { }

        public void Exit(bool returnToGame) { }

        public void OpenRsiPage(string url) { }

        public void CloseDossier() { }

        public IReadOnlyList<SearchHit> Search(string text) => Array.Empty<SearchHit>();

        public void OpenDossierFor(string handle) { }

        public void LookUp(string handle) { }

        public bool SaveNote(string handle, string? citizenRecord, string text) => true;

        public void RefreshDossier() { }

        public string? NoteFor(string handle) => null;
    }
}
