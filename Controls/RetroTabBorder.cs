using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// A document tab's face. On 98SE with the strip below the page it also draws the tab's Win98
    /// edges, the same way KillerShell's tabs do.
    /// </summary>
    /// <remarks>
    /// The template flips a bottom strip's tabs upside down, and flipping the top-strip bevels
    /// would flip Win98's light, which always comes from the top-left. So on a bottom strip the
    /// template's bevel borders are hidden and the edges are drawn here pixel by pixel: white left
    /// edge, #808080 then black on the right and along the foot, the shadow owning both foot
    /// corners, which are cut on the diagonal. No top edge: the tab hangs from the page.
    /// </remarks>
    public sealed class RetroTabBorder : Border
    {
        public static readonly DependencyProperty RetroBottomEdgesProperty = DependencyProperty.Register(
            nameof(RetroBottomEdges), typeof(bool), typeof(RetroTabBorder),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool RetroBottomEdges
        {
            get => (bool)GetValue(RetroBottomEdgesProperty);
            set => SetValue(RetroBottomEdgesProperty, value);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (RetroBottomEdges) DrawRetroBottomEdges(drawingContext);
        }

        private void DrawRetroBottomEdges(DrawingContext dc)
        {
            int w = (int)Math.Round(RenderSize.Width), h = (int)Math.Round(RenderSize.Height);
            if (w < 6 || h < 4) return;
            // Drawn in screen orientation: undo the template's flip.
            dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, h));
            Brush light = (TryFindResource("BevelLightBrush") as Brush) ?? Brushes.White;
            Brush shadow = (TryFindResource("BevelDarkBrush") as Brush) ?? Brushes.Gray;
            Brush dark = (TryFindResource("BevelOuterDarkBrush") as Brush) ?? Brushes.Black;
            Brush face = (TryFindResource("BackgroundBrush") as Brush) ?? Brushes.Silver;
            void Px(Brush b, int x, int y, int cw = 1, int ch = 1) => dc.DrawRectangle(b, null, new Rect(x, y, cw, ch));

            // Cut corners show the strip behind the tab.
            Px(face, 0, h - 2, 1, 2); Px(face, 1, h - 1);
            Px(face, w - 1, h - 2, 1, 2); Px(face, w - 2, h - 1);
            Px(light, 0, 0, 1, h - 2);            // left highlight
            Px(light, 1, h - 2);                  // its diagonal into the foot
            Px(shadow, w - 2, 0, 1, h - 2);       // right, inner
            Px(dark, w - 1, 0, 1, h - 2);         // right, outer
            Px(shadow, 2, h - 2, w - 4, 1);       // foot, inner
            Px(dark, 2, h - 1, w - 4, 1);         // foot, outer
            Px(dark, w - 2, h - 2);               // the shadow's diagonal
            dc.Pop();
        }
    }
}
