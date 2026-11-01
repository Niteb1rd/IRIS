namespace Iris.Core;

public sealed class StartupMilestones
{
    public const string Prefix = "Startup check: ";

    private readonly ILog _log;
    private readonly IClock _clock;
    private int _frame;
    private int _captureFailed;
    private int _plate;
    private int _name;
    private int _rsi;
    private int _rsiFailed;
    private int _bracket;

    public StartupMilestones(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
    }

    public bool FrameSeen => Volatile.Read(ref _frame) != 0;

    public bool PipelineDone => Volatile.Read(ref _plate) != 0 && Volatile.Read(ref _name) != 0;

    public void FrameReceived(int width, int height, double maxValue) =>
        Once(ref _frame, Pipeline.BlankPictureWatch.IsBlack(maxValue)
            ? $"capture works, first frame {width}x{height}, but the picture is blank (all black): the game may run on another graphics card, or something blocks capture"
            : $"capture works, first frame {width}x{height}");

    public void CaptureFailed(string reason)
    {
        if (!FrameSeen)
            Once(ref _captureFailed, "capture failed: " + reason);
    }

    public void PlateFound() => Once(ref _plate, "first nameplate found");

    public void NameConfirmed() => Once(ref _name, "first name read and confirmed");

    public void RsiAnswered() => Once(ref _rsi, "RSI lookups work (first answer received)");

    public void RsiFailed(string reason) => Once(ref _rsiFailed, "RSI lookup failed: " + reason);

    public void OnRsiAnswer(Lookup.RsiStatus status, string? reason)
    {
        if (status == Lookup.RsiStatus.Failed)
            RsiFailed(reason ?? "no reason given");
        else
            RsiAnswered();
    }

    public void BracketDrawn() => Once(ref _bracket, "overlay works, first bracket drawn");

    private void Once(ref int flag, string text)
    {
        if (Interlocked.Exchange(ref flag, 1) == 0)
            _log.Write(_clock.UtcNow, Prefix + text);
    }
}
