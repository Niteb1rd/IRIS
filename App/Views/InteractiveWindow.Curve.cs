
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Iris.Core.Overlay;

namespace Iris.App.Views;

public sealed partial class InteractiveWindow
{
    private const double CurveRowPx = 16;

    private const int CurveColumns = 4;

    private const double HostPadPx = 12;

    private double _curveDepth;
    private readonly HostCanvas _listHost = NewHost();
    private readonly HostCanvas _dossierHost = NewHost();
    private Rect _listArea;
    private Rect _dossierArea;
    private readonly Canvas _anchors = new() { IsHitTestVisible = false };
    private FrameworkElement? _searchAnchor;

    private bool Curved => _curveDepth > 0;

    private static HostCanvas NewHost() => new() { Background = Brushes.Transparent, ClipToBounds = true };

    private sealed class HostCanvas : Canvas
    {
        public LayoutClock? Clock { get; set; }

        protected override Size MeasureOverride(Size constraint) => Clock is { } clock ? clock.Time(() => base.MeasureOverride(constraint)) : base.MeasureOverride(constraint);

        protected override Size ArrangeOverride(Size arrangeSize) => Clock is { } clock ? clock.Time(() => base.ArrangeOverride(arrangeSize)) : base.ArrangeOverride(arrangeSize);
    }

    private void PlaceHosts()
    {
        var pad = HostPadPx * Look;
        var window = new Rect(0, 0, _root.Width, _root.Height);
        var (left, top, listWidth, bottom) = PanelPx();
        var shift = PlayPanelShift();
        var x0 = left / Scale + Math.Min(0, shift);
        var x1 = (left + listWidth) / Scale + Math.Max(0, shift);
        _listArea = Within(new Rect(x0 - pad, top / Scale - pad, x1 - x0 + 2 * pad, (bottom - top) / Scale + 2 * pad), window);
        var dossier = DossierPx();
        _dossierArea = dossier.IsEmpty
            ? Rect.Empty
            : Within(new Rect(dossier.Left / Scale - pad, dossier.Top / Scale - pad, dossier.Width / Scale + 2 * pad, (dossier.Bottom - dossier.Top) / Scale + 2 * pad), window);
        SizeHost(_listHost, _listArea);
        SizeHost(_dossierHost, _dossierArea);
    }

    private static void SizeHost(Canvas host, Rect area)
    {
        host.Width = area.IsEmpty ? 0 : area.Width;
        host.Height = area.IsEmpty ? 0 : area.Height;
    }

    private static Rect Within(Rect area, Rect window)
    {
        area.Intersect(window);
        return area;
    }

    private double ListHostLeft(double windowLeft) => windowLeft - _listArea.X;

    private double ListHostTop(double windowTop) => windowTop - _listArea.Y;

    private double DossierHostLeft(double windowLeft) => windowLeft - _dossierArea.X;

    private double DossierHostTop(double windowTop) => windowTop - _dossierArea.Y;

    private void ShowContent()
    {
        if (Curved)
        {
            _stage.Children.Add(CurvedSurface());
            return;
        }
        foreach (var (host, area) in new[] { (_listHost, _listArea), (_dossierHost, _dossierArea) })
        {
            if (area.IsEmpty)
                continue;
            Canvas.SetLeft(host, area.X);
            Canvas.SetTop(host, area.Y);
            _root.Children.Add(host);
        }
        _stage.Children.Add(_root);
    }

    private UIElement CurvedSurface()
    {
        var width = _root.Width;
        var height = _root.Height;
        var viewport = new Viewport3D
        {
            Width = width,
            Height = height,
            ClipToBounds = true,
            Camera = new OrthographicCamera(new Point3D(width / 2, -height / 2, 10), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), width)
            {
                NearPlaneDistance = 1,
                FarPlaneDistance = 20,
            },
        };
        viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
        foreach (var (host, area) in new[] { (_dossierHost, _dossierArea), (_listHost, _listArea) })
        {
            if (area.IsEmpty || area.Width <= 0 || area.Height <= 0)
                continue;
            var material = new DiffuseMaterial(Brushes.White);
            Viewport2DVisual3D.SetIsVisualHostMaterial(material, true);
            viewport.Children.Add(new Viewport2DVisual3D { Geometry = Mesh(area), Material = material, Visual = host });
        }

        _anchors.Width = width;
        _anchors.Height = height;
        var stage = new Grid();
        stage.Children.Add(viewport);
        stage.Children.Add(_anchors);
        return stage;
    }

    private MeshGeometry3D Mesh(Rect area)
    {
        var mesh = new MeshGeometry3D();
        var rows = Math.Max(1, (int)Math.Ceiling(area.Height * Scale / CurveRowPx));
        for (var r = 0; r <= rows; r++)
        {
            var y = area.Top + area.Height * r / rows;
            for (var c = 0; c <= CurveColumns; c++)
            {
                var x = area.Left + area.Width * c / CurveColumns;
                var (drawnX, drawnY) = ScreenCurve.Map(x * Scale, y * Scale, _bounds.Width, _bounds.Height, _curveDepth);
                mesh.Positions.Add(new Point3D(drawnX / Scale, -drawnY / Scale, 0));
                mesh.TextureCoordinates.Add(new Point((double)c / CurveColumns, (double)r / rows));
            }
        }
        var stride = CurveColumns + 1;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < CurveColumns; c++)
            {
                var topLeft = r * stride + c;
                var bottomLeft = topLeft + stride;
                mesh.TriangleIndices.Add(topLeft);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(bottomLeft + 1);
                mesh.TriangleIndices.Add(topLeft);
                mesh.TriangleIndices.Add(bottomLeft + 1);
                mesh.TriangleIndices.Add(topLeft + 1);
            }
        }
        mesh.Freeze();
        return mesh;
    }

    private UIElement SearchTarget(UIElement box)
    {
        if (!Curved)
            return box;
        if (_searchAnchor is null)
        {
            _searchAnchor = new Border { Background = Brushes.Transparent };
            _anchors.Children.Add(_searchAnchor);
        }
        return _searchAnchor;
    }

    private void PlaceSearchAnchor(FrameworkElement box, Popup popup, FrameworkElement dropDown)
    {
        if (!Curved || _searchAnchor is null)
            return;
        if (box.ActualWidth <= 0 || PresentationSource.FromVisual(box) is null)
        {
            popup.PlacementTarget = box;
            return;
        }
        var flat = box.TransformToAncestor(_listHost).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
        flat.Offset(_listArea.X, _listArea.Y);
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var corner in new[] { flat.TopLeft, flat.TopRight, flat.BottomLeft, flat.BottomRight })
        {
            var (x, y) = ScreenCurve.Map(corner.X * Scale, corner.Y * Scale, _bounds.Width, _bounds.Height, _curveDepth);
            left = Math.Min(left, x / Scale);
            right = Math.Max(right, x / Scale);
            top = Math.Min(top, y / Scale);
            bottom = Math.Max(bottom, y / Scale);
        }
        Canvas.SetLeft(_searchAnchor, left);
        Canvas.SetTop(_searchAnchor, top);
        _searchAnchor.Width = Math.Max(1, right - left);
        _searchAnchor.Height = Math.Max(1, bottom - top);
        _searchAnchor.UpdateLayout();
        popup.PlacementTarget = _searchAnchor;
        dropDown.MinWidth = Math.Max(dropDown.MinWidth, _searchAnchor.Width);
    }
}
