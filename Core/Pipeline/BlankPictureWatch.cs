namespace Iris.Core.Pipeline;

public sealed class BlankPictureWatch
{
    public const string NoteText = "IRIS: Video captured from Star Citizen is black. No OCR possible.";

    private double _lastCheckMs = double.NegativeInfinity;
    private double _blackSinceMs = double.NaN;

    public bool Active { get; private set; }

    public static bool IsBlack(double brightestValue) => brightestValue <= LiveSettings.BlankFrameMaxValue;

    public bool Due(double nowMs) => nowMs - _lastCheckMs >= LiveSettings.BlankCheckEveryMs;

    public bool Update(double nowMs, double brightestValue)
    {
        _lastCheckMs = nowMs;
        if (!IsBlack(brightestValue))
            _blackSinceMs = double.NaN;
        else if (double.IsNaN(_blackSinceMs))
            _blackSinceMs = nowMs;

        var active = !double.IsNaN(_blackSinceMs) && nowMs - _blackSinceMs >= LiveSettings.BlankNoteAfterMs;
        var changed = active != Active;
        Active = active;
        return changed;
    }

    public void Reset()
    {
        _lastCheckMs = double.NegativeInfinity;
        _blackSinceMs = double.NaN;
        Active = false;
    }
}
