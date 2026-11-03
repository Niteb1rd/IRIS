using Iris.Core.Overlay;

namespace Iris.Core.Mining;

public sealed record MiningState(
    double LockedMs, double RowTop, PixelRect SignalBox, double DigitHeight, Bgr TextColour, Bgr BackColour, int? Value, string? Label)
{
    public static MiningState FromLock(SignalRead read, double frameMs, MiningTable table)
    {
        var label = read.Value is { } value ? table.Lookup(value) : null;
        var back = LabelColours.Readable(read.TextColour, read.BackColour, MiningSettings.Default.MinContrast);
        return new MiningState(frameMs, read.RowTop, read.Box, read.DigitHeight, read.TextColour, back, label is null ? null : read.Value, label);
    }
}

public sealed class MiningLabelState
{
    private readonly MiningSettings _settings;
    private double _lastSeenMs;
    private double _lastValueMs;
    private int? _pending;
    private int _pendingCount;

    public MiningLabelState(MiningSettings settings, double lockedMs, int? value, string? label)
    {
        _settings = settings;
        _lastSeenMs = lockedMs;
        _lastValueMs = lockedMs;
        Value = value;
        Label = label;
    }

    public int? Value { get; private set; }

    public string? Label { get; private set; }

    public bool Ended { get; private set; }

    public bool Apply(SignalRead read, double frameMs, MiningTable table)
    {
        if (Ended)
            return false;
        var changed = false;
        switch (read.State)
        {
            case SignalState.Signal when read.Value is { } value:
                _lastSeenMs = Math.Max(_lastSeenMs, frameMs);
                if (value == Value)
                {
                    _lastValueMs = Math.Max(_lastValueMs, frameMs);
                    _pending = null;
                    break;
                }

                var label = table.Lookup(value);
                var needed = Value is not null
                    ? IsClearlyDifferent(read, Value.Value, _settings.ClearDigitMargin) ? 1 : _settings.ChangeReads
                    : label is not null ? 1 : _settings.OffTableReads;
                if (_pending == value)
                    _pendingCount++;
                else
                {
                    _pending = value;
                    _pendingCount = 1;
                }

                if (_pendingCount >= needed)
                {
                    _pending = null;
                    _lastValueMs = frameMs;
                    changed = Show(value, label);
                }

                break;

            case SignalState.Unreadable:
                _lastSeenMs = Math.Max(_lastSeenMs, frameMs);
                break;

            case SignalState.Disqualified:
                _lastSeenMs = Math.Max(_lastSeenMs, frameMs);
                _pending = null;
                changed = Show(null, null);
                break;

            default:
                _pending = null;
                if (frameMs - _lastSeenMs >= _settings.HoldMs)
                {
                    Ended = true;
                    Value = null;
                    Label = null;
                    return true;
                }

                break;
        }

        if (Value is not null && frameMs - _lastValueMs >= _settings.HoldMs)
            changed |= Show(null, null);
        return changed;
    }

    public static bool IsClearlyDifferent(SignalRead read, int shown, double margin)
    {
        if (read.Value is not { } value || read.DigitScores is not { } scores)
            return false;
        var readDigits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shownDigits = shown.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (readDigits.Length != shownDigits.Length)
            return true;
        if (scores.Count != readDigits.Length)
            return false;
        for (var i = 0; i < readDigits.Length; i++)
        {
            if (readDigits[i] == shownDigits[i])
                continue;
            var digitScores = scores[i];
            if (digitScores[readDigits[i] - '0'] - digitScores[shownDigits[i] - '0'] < margin)
                return false;
        }

        return true;
    }

    private bool Show(int? value, string? label)
    {
        var changed = value != Value || label != Label;
        Value = value;
        Label = label;
        return changed;
    }
}
