using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Iris.Core.Overlay;
using Forms = System.Windows.Forms;

namespace Iris.App.Services;

internal sealed class TrayMenuLook : Forms.ToolStripProfessionalRenderer
{
    private static readonly Color Back = FromArgb(PanelLook.MenuArgb);
    private static readonly Color Highlight = FromArgb(PanelLook.MenuHighlightArgb);
    private static readonly Color Border = FromArgb(PanelLook.MenuBorderArgb);
    private static readonly Color Text = FromArgb(PanelLook.TextArgb);
    private static readonly Color Disabled = FromArgb(PanelLook.SecondaryTextArgb);

    private TrayMenuLook()
        : base(new MenuColors()) => RoundedEdges = false;

    public static void Apply(Forms.ContextMenuStrip menu)
    {
        menu.Renderer = new TrayMenuLook();
        menu.BackColor = Back;
        menu.ForeColor = Text;
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        if (!e.Item.Enabled)
        {
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, Disabled, e.TextFormat);
            return;
        }
        e.TextColor = Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        if (r.Width <= 0 || r.Height <= 0)
            return;
        var g = e.Graphics;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Text, Math.Max(1.5f, r.Height / 9f)))
        {
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f),
            });
        }
        g.SmoothingMode = smoothing;
    }

    private static Color FromArgb(uint argb) => Color.FromArgb((int)(argb >> 24), (int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));

    private sealed class MenuColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Highlight;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color MenuItemPressedGradientBegin => Highlight;
        public override Color MenuItemPressedGradientMiddle => Highlight;
        public override Color MenuItemPressedGradientEnd => Highlight;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Back;
    }
}
