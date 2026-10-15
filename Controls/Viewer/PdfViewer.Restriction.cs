using System.Windows;
using System.Windows.Controls;
using KillerFamily.Accessibility;

namespace KillerPDF.Controls
{
    // The owner-restriction notice. A PDF whose owner password forbids editing opens read-only,
    // and this bar says so at the top of the pane with a button that asks for the owner password.
    // It follows the active tab, so RebuildTabStrip refreshes it on every open, switch and close.
    public partial class PdfViewer
    {
        private Border? _restrictionNotice;

        /// <summary>True while the active tab is a protected PDF opened read-only because its owner
        /// restricts editing.</summary>
        internal bool IsOwnerRestricted => _doc?.IsReadOnly == true && _openedFromProtected;

        internal void UpdateRestrictionNotice()
        {
            if (!IsOwnerRestricted)
            {
                if (_restrictionNotice is not null) _restrictionNotice.Visibility = Visibility.Collapsed;
                return;
            }
            if (_restrictionNotice is null)
            {
                // Hosted where the annotation bars live, above the document and below the pane bevel.
                if (PagePreviewPanel.Parent is not Grid previewArea || Host is null) return;
                _restrictionNotice = BuildRestrictionNotice(Host);
                previewArea.Children.Add(_restrictionNotice);
            }
            _restrictionNotice.Visibility = Visibility.Visible;
        }

        // Same surface tokens, grain host and shadow as the floating annotation bars, centered at
        // the top of the pane.
        private static Border BuildRestrictionNotice(Features.IViewerHost host)
        {
            var lockGlyph = new Glyph
            {
                Text = "\uE72E",
                FontFamily = UiKit.IconFont,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            lockGlyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var message = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            message.SetResourceReference(TextBlock.TextProperty, "Str_Restricted_Notice");
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var unlock = UiKit.Make(host.Loc("Str_Restricted_EnterPassword"), accent: true);
            unlock.SetResourceReference(ContentControl.ContentProperty, "Str_Restricted_EnterPassword");
            unlock.Margin = new Thickness(12, 0, 0, 0);
            unlock.VerticalAlignment = VerticalAlignment.Center;
            unlock.Click += (_, _) => host.UnlockOwnerRestrictions();

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 2, 2, 2) };
            DockPanel.SetDock(lockGlyph, Dock.Left);
            DockPanel.SetDock(unlock, Dock.Right);
            row.Children.Add(lockGlyph);
            row.Children.Add(unlock);
            row.Children.Add(message);

            var bar = new Border
            {
                Child = host.BuildBarHost(row),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = 640,
                Effect = host.AnnotBarShadow()
            };
            bar.SetResourceReference(Border.BackgroundProperty, "AnnotationBarBrush");
            bar.SetResourceReference(Border.CornerRadiusProperty, "AnnotationBarCornerRadius");
            bar.SetResourceReference(Border.BorderBrushProperty, "AnnotationBarBorderBrush");
            bar.SetResourceReference(Border.BorderThicknessProperty, "BarEdgeThickness");
            bar.SetResourceReference(Border.PaddingProperty, "BarPadding");
            Panel.SetZIndex(bar, 90);
            return bar;
        }
    }
}
