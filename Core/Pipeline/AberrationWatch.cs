using Iris.Core.Detection;

namespace Iris.Core.Pipeline;

public enum AberrationState
{
    Unknown,

    On,

    Off,
}

public sealed class AberrationWatch
{
    public const string NoteText = "IRIS: Chromatic Aberration is on in Star Citizen's graphics settings. Names read better with it off.";

    private readonly Queue<bool> _recent = new();

    public static LayerRule RuleFor(AberrationState state, bool measures) => state switch
    {
        AberrationState.On => LayerRule.Agree,
        AberrationState.Unknown when measures => LayerRule.Measure,
        _ => LayerRule.Brightest,
    };

    public AberrationState? Add(AberrationState state, IReadOnlyList<bool> verdicts)
    {
        if (state == AberrationState.Off)
        {
            _recent.Clear();
            return null;
        }

        foreach (var shifted in verdicts)
        {
            _recent.Enqueue(shifted);
            while (_recent.Count > Math.Max(LiveSettings.AberrationOnCrops, LiveSettings.AberrationOffCrops))
                _recent.Dequeue();
            if (state == AberrationState.Unknown && Count(LiveSettings.AberrationOnCrops, true) >= LiveSettings.AberrationOnShifted)
            {
                _recent.Clear();
                return AberrationState.On;
            }

            if (state == AberrationState.On && Count(LiveSettings.AberrationOffCrops, false) >= LiveSettings.AberrationOffUnshifted)
            {
                _recent.Clear();
                return AberrationState.Off;
            }
        }

        return null;
    }

    private int Count(int n, bool value)
    {
        var skip = Math.Max(0, _recent.Count - n);
        var count = 0;
        foreach (var verdict in _recent.Skip(skip))
            if (verdict == value)
                count++;
        return count;
    }
}
