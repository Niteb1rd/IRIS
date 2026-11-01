namespace Iris.Core.Overlay;

public enum GameWindowState
{
    GameNotActive,

    NotBorderless,

    Ready,
}

public static class GameWindowRules
{
    public static GameWindowState Decide(bool isStarCitizenInFront, WindowMode? gameMode)
    {
        if (!isStarCitizenInFront || gameMode is null)
            return GameWindowState.GameNotActive;
        return gameMode == WindowMode.Borderless ? GameWindowState.Ready : GameWindowState.NotBorderless;
    }
}

public sealed class BorderlessNoticeTracker
{
    public bool WasShown { get; private set; }

    public bool ShouldShow(GameWindowState state, bool overlayDisabled, bool otherPopUpOpen, WindowMode? gameMode = null) =>
        state == GameWindowState.NotBorderless && gameMode != WindowMode.Borderless && !WasShown && !overlayDisabled && !otherPopUpOpen;

    public void MarkShown() => WasShown = true;

    public const string Text = "IRIS requires Borderless mode to function. Change in Options > Graphics > Window Mode.";
}
