using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Wgc = Windows.Graphics.Capture;
using Wsa = Windows.Security.Authorization.AppCapabilityAccess;

namespace Iris.App.Services;

internal static class CaptureBorder
{
    private const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string AccessType = "Windows.Graphics.Capture.GraphicsCaptureAccess";

    private const int Unknown = 0;
    private const int Asking = 1;
    private const int Allowed = 2;
    private const int Refused = 3;
    private const int NotSupported = 4;

    private static int _state = Unknown;
    private static readonly Lazy<bool> Supported = new(DetectSupport, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAllowed => Volatile.Read(ref _state) == Allowed;

    public static bool IsSupported => Supported.Value;

    public static void RequestAccess(Action<string>? log)
    {
        var previous = Volatile.Read(ref _state);
        if (previous is not (Unknown or Refused) || Interlocked.CompareExchange(ref _state, Asking, previous) != previous)
            return;
        if (!Supported.Value)
        {
            Volatile.Write(ref _state, NotSupported);
            return;
        }

        _ = Task.Run(async () =>
        {
            int result;
            string line;
            try
            {
                var answer = await Wgc.GraphicsCaptureAccess.RequestAccessAsync(Wgc.GraphicsCaptureAccessKind.Borderless).AsTask().ConfigureAwait(false);
                result = answer == Wsa.AppCapabilityAccessStatus.Allowed ? Allowed : Refused;
                line = result == Allowed
                    ? "Capture border: Windows allows hiding it"
                    : $"Capture border: Windows did not allow hiding it ({answer}); it stays";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = NotSupported;
                line = "Capture border: the request to hide it failed (" + ex.GetType().Name + ": " + ex.Message + "); it stays";
            }

            Volatile.Write(ref _state, result);
            if (previous != Refused || result != Refused)
                log?.Invoke(line);
        });
    }

    public static bool WantsBorderless(bool settingOn) => settingOn && IsAllowed;

    public static void Apply(Wgc.GraphicsCaptureSession session, bool settingOn, bool borderless, Action<string>? log)
    {
        if (!settingOn || !Supported.Value)
            return;
        if (!borderless)
        {
            var state = Volatile.Read(ref _state);
            log?.Invoke(state switch
            {
                NotSupported => "Capture border: shown (not supported)",
                Refused => "Capture border: shown (access denied)",
                _ => "Capture border: shown (waiting for Windows to allow hiding it)",
            });
            return;
        }

        try
        {
            session.IsBorderRequired = false;
            log?.Invoke("Capture border: hidden");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Invoke("Capture border: shown (Windows refused to hide it: " + ex.GetType().Name + ")");
        }
    }

    private static bool DetectSupport()
    {
        try
        {
            return ApiInformation.IsTypePresent(AccessType)
                && ApiInformation.IsMethodPresent(AccessType, "RequestAccessAsync")
                && ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
