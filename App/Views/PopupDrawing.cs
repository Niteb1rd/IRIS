
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Iris.App.Services;

namespace Iris.App.Views;

internal static class PopupDrawing
{
    private static readonly ConditionalWeakTable<HwndSource, object> Seen = new();

    public static void DrawLayeredOnCpu() =>
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not FrameworkElement element || VisualTreeHelper.GetParent(element) is not null
                || PresentationSource.FromVisual(element) is not HwndSource { CompositionTarget: { } target } source
                || !Seen.TryAdd(source, source))
                return;
            var style = NativeMethods.GetWindowLongPtr(source.Handle, NativeMethods.GwlExStyle).ToInt64();
            if ((style & NativeMethods.WsExLayered) != 0)
                target.RenderMode = RenderMode.SoftwareOnly;
        }));
}
