using System.Globalization;
using System.Windows.Threading;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>
    /// What the sidebar's Comments panel needs from this viewer: its measurements in page order,
    /// their labels, and a way to bring one into view selected.
    /// </summary>
    public partial class PdfViewer
    {
        internal List<MeasurementAnnotation> MeasurementsInOrderExt() =>
            [.. _annotations.OrderBy(page => page.Key)
                .SelectMany(page => page.Value.OfType<MeasurementAnnotation>())
                .Where(MeasurementGeometry.IsUsable)];

        internal string MeasurementLabelExt(MeasurementAnnotation measurement) =>
            TryPointsPerCanvasUnit(measurement.PageIndex, out double pointsPerUnit) && pointsPerUnit > 0
                ? MeasurementGeometry.Label(measurement, pointsPerUnit, CultureInfo.CurrentCulture)
                : "";

        /// <summary>Goes to the measurement's page and selects it, with its handles.</summary>
        internal void RevealMeasurementExt(MeasurementAnnotation measurement)
        {
            if (_doc is null) return;
            NavigateToPageExt(measurement.PageIndex);
            // The page's canvas exists once the navigation has laid out.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                if (!_annotations.TryGetValue(measurement.PageIndex, out var list) || !list.Contains(measurement)) return;
                ClearSelection();
                RenderAllAnnotations(measurement.PageIndex);
                _activeCanvas = CanvasForPage(measurement.PageIndex);
                SelectAnnotation(measurement, AnnotBounds(measurement));
            }));
        }
    }
}
