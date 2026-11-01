using System.Data.Common;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Lookup;
using Iris.Core.Overlay;

namespace Iris.Core.Pipeline;

public sealed class PlateNameJudge : IReadJudge
{
    private readonly ExclusionList _exclusions;
    private readonly SessionExclusions _session;
    private readonly Func<string, IReadOnlyList<OrgMembership>?> _lookupOrgs;
    private readonly Func<string, string, bool>? _isOrgMember;
    private readonly LiveSettings _live;
    private readonly Dictionary<string, (double AtMs, IReadOnlyList<OrgMembership>? Orgs)> _memo = new(StringComparer.OrdinalIgnoreCase);

    public PlateNameJudge(
        ExclusionList exclusions, SessionExclusions session, Func<string, IReadOnlyList<OrgMembership>?> lookupOrgs, Func<string, string, bool>? isOrgMember, LiveSettings live)
    {
        _exclusions = exclusions;
        _session = session;
        _lookupOrgs = lookupOrgs;
        _isOrgMember = isOrgMember;
        _live = live;
    }

    private SocialConfig _config = new();
    private readonly Dictionary<string, (double AtMs, bool Listed)> _listed = new(StringComparer.OrdinalIgnoreCase);

    public SocialConfig Config
    {
        get => _config;
        set
        {
            if (ReferenceEquals(_config, value))
                return;
            _config = value;
            _listed.Clear();
        }
    }

    public double NowMs { get; set; }

    public NameClass Classify(string name)
    {
        if (!RsiNames.IsValidHandle(name))
            return NameClass.Excluded;
        if (!NameFilter.IsOnPlayerList(name, Config))
        {
            if (ContactCode.IsContactCode(name))
                return NameClass.Contact;
            if (_exclusions.IsExcluded(name))
                return NameClass.Excluded;
        }

        if (_session.Contains(name) && !IsListed(name))
            return NameClass.Excluded;
        return NameClass.Valid;
    }

    public bool IsListed(string name)
    {
        if (_listed.TryGetValue(name, out var known) && NowMs - known.AtMs < _live.OrgCacheMs)
            return known.Listed;
        var listed = ComputeListed(name);
        if (_listed.Count >= _live.OrgCacheEntries)
            _listed.Clear();
        _listed[name] = (NowMs, listed);
        return listed;
    }

    private bool ComputeListed(string name)
    {
        if (NameFilter.IsOnPlayerList(name, Config))
            return true;
        var orgs = MemoizedOrgs(name);
        if (orgs is not null && ColorResolver.Resolve(name, orgs, Config).Kind is MatchKind.PlayerList or MatchKind.OrgList)
            return true;
        if (_isOrgMember is null)
            return false;
        try
        {
            return Config.Orgs.Any(org => _isOrgMember(org.Sid, name));
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    public void ForgetMemo()
    {
        _memo.Clear();
        _listed.Clear();
    }

    private IReadOnlyList<OrgMembership>? MemoizedOrgs(string handle)
    {
        if (_memo.TryGetValue(handle, out var known) && NowMs - known.AtMs < _live.OrgCacheMs)
            return known.Orgs;
        IReadOnlyList<OrgMembership>? orgs;
        try
        {
            orgs = _lookupOrgs(handle);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            orgs = null;
        }

        if (_memo.Count >= _live.OrgCacheEntries)
            _memo.Clear();
        _memo[handle] = (NowMs, orgs);
        return orgs;
    }
}
