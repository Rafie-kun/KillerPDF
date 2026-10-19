using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private Dock? _railShadowEdge;

    private void InitializeRailShadow()
    {
        RailPaneShadow.SetBinding(EffectProperty, new Binding(nameof(Effect))
        {
            Source = PaneShadow,
            Converter = new CenteredPaneShadowConverter(),
        });
        CardRow.SizeChanged += (_, _) => UpdateRailShadowClips();
    }

    internal void SetRailShadowEdge(Dock? edge)
    {
        _railShadowEdge = edge is Dock.Top or Dock.Bottom ? edge : null;
        UpdateRailShadowClips();
    }

    private void UpdateRailShadowClips()
    {
        if (_railShadowEdge is null)
        {
            PaneShadowClip.Clip = null;
            RailPaneShadowClip.Visibility = Visibility.Collapsed;
            return;
        }
        double width = CardRow.ActualWidth, height = CardRow.ActualHeight;
        if (width <= 0 || height <= 0) return;
        const double padding = 128;
        double middle = height / 2;
        var top = new RectangleGeometry(new Rect(-padding, -padding, width + 2 * padding, middle + padding));
        var bottom = new RectangleGeometry(new Rect(-padding, middle, width + 2 * padding, height - middle + padding));
        // Parent clips crop the composited child effects, keeping the other half unchanged.
        PaneShadowClip.Clip = _railShadowEdge == Dock.Top ? bottom : top;
        RailPaneShadowClip.Clip = _railShadowEdge == Dock.Top ? top : bottom;
        RailPaneShadowClip.Visibility = Visibility.Visible;
    }

    private sealed class CenteredPaneShadowConverter : IValueConverter
    {
        public object? Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not DropShadowEffect source) return null;
            var effect = source.CloneCurrentValue();
            effect.ShadowDepth = 0;
            effect.Freeze();
            return effect;
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
