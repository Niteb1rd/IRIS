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

    public const string Text =
        "Star Citizen is not in borderless windowed mode, so the IRIS overlay is turned off. Switch to Borderless in the game's graphics settings.";

    public static string TextFor(WindowMode? gameMode) => gameMode switch
    {
        WindowMode.Fullscreen => "Star Citizen is in Fullscreen mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings.",
        WindowMode.Windowed => "Star Citizen is in Windowed mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings.",
        _ => Text,
    };
}
