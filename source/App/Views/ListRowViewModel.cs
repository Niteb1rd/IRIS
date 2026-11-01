using System;
using Iris.Core.Config;
using Iris.Core.Lookup;

namespace Iris.App.Views;

public enum RowStatusKind
{
    Empty,

    Unchecked,

    Checking,

    Valid,
    NotFound,

    Invalid,

    Failed,
}

public abstract class ListRowViewModel : ObservableObject
{
    private string _value;
    private OverlayColor _color;
    private RowStatusKind _statusKind;
    private string _statusText = string.Empty;
    private string _note = string.Empty;

    protected ListRowViewModel(string value, OverlayColor color)
    {
        _value = value;
        _color = color;
        _statusKind = value.Trim().Length == 0 ? RowStatusKind.Empty : RowStatusKind.Unchecked;
    }

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? string.Empty))
                return;
            Version++;
            IsFlaggedDuplicate = false;
            SetStatus(_value.Trim().Length == 0 ? RowStatusKind.Empty : RowStatusKind.Unchecked, string.Empty);
            OnValueEdited();
        }
    }

    public OverlayColor Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    public int Version { get; private set; }

    public bool IsFlaggedDuplicate { get; private set; }

    public RowStatusKind StatusKind => _statusKind;

    public string StatusText => _statusText;

    public bool IsChecking => _statusKind == RowStatusKind.Checking;

    public string Note
    {
        get => _note;
        protected set
        {
            if (Set(ref _note, value))
                Raise(nameof(HasNote));
        }
    }

    public bool HasNote => _note.Length > 0;

    protected virtual void OnValueEdited()
    {
    }

    public void BeginCheck() => SetStatus(RowStatusKind.Checking, "Checking…");

    public void ResetStatus()
    {
        IsFlaggedDuplicate = false;
        SetStatus(_value.Trim().Length == 0 ? RowStatusKind.Empty : RowStatusKind.Unchecked, string.Empty);
    }

    public void ApplyResult(RowCheckResult result)
    {
        IsFlaggedDuplicate = result.State == RowCheckState.InvalidFormat && result.Message == RowValidator.DuplicateMessage;
        switch (result.State)
        {
            case RowCheckState.Valid:
                SetStatus(RowStatusKind.Valid, "✓ " + (result.DisplayText ?? result.Message));
                break;
            case RowCheckState.NotFound:
                SetStatus(RowStatusKind.NotFound, "Not found on RSI");
                break;
            case RowCheckState.InvalidFormat:
                SetStatus(RowStatusKind.Invalid, result.Message);
                break;
            default:
                SetStatus(RowStatusKind.Failed, result.Message);
                break;
        }
    }

    private void SetStatus(RowStatusKind kind, string text)
    {
        _statusKind = kind;
        _statusText = text;
        Raise(nameof(StatusKind));
        Raise(nameof(StatusText));
        Raise(nameof(IsChecking));
    }
}

public sealed class PlayerRowViewModel : ListRowViewModel
{
    public const string ExclusionNote = "On the exclusion list: still matched because it is on your player list";

    private readonly Func<string, bool> _isExcluded;

    public PlayerRowViewModel(string handle, OverlayColor color, Func<string, bool> isExcluded)
        : base(handle, color)
    {
        _isExcluded = isExcluded;
        UpdateNote();
    }

    protected override void OnValueEdited() => UpdateNote();

    public void RefreshExclusionNote() => UpdateNote();

    private void UpdateNote()
    {
        var text = Value.Trim();
        Note = RsiNames.IsValidHandle(text) && _isExcluded(text) ? ExclusionNote : string.Empty;
    }
}

public sealed class OrgRowViewModel : ListRowViewModel
{
    private int _priority;

    public OrgRowViewModel(string sid, int priority, OverlayColor color)
        : base(sid, color)
    {
        _priority = priority;
    }

    public int Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }
}
