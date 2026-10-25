using System.Diagnostics;
using System.Windows.Automation;

namespace KillerPDF.LatencySmoke;

internal static class ZoomPreset
{
    internal static void Select200Percent(IntPtr hwnd, int processId)
        => Select(hwnd, processId, "200%");

    internal static void SelectFitWidth(IntPtr hwnd, int processId)
        => Select(hwnd, processId, "Fit Width");

    private static void Select(IntPtr hwnd, int processId, string name)
    {
        AutomationElement window = AutomationElement.FromHandle(hwnd);
        if (window.Current.ProcessId != processId)
            throw new TimeoutException("Zoom window does not belong to the measured process.");
        AutomationElement? combo = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ZoomBox"));
        if (combo is null || !combo.Current.IsEnabled
            || !combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expansion))
            throw new TimeoutException("The measured window has no enabled zoom selector.");
        var dropdown = (ExpandCollapsePattern)expansion;
        try
        {
            dropdown.Expand();
            var deadline = Stopwatch.StartNew();
            AutomationElement? preset;
            do
            {
                preset = combo.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, name)));
                if (preset is not null) break;
                Thread.Sleep(20);
            } while (deadline.ElapsedMilliseconds < 5000);
            if (preset is null || !preset.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection))
                throw new TimeoutException($"The measured zoom selector has no {name} preset.");
            ((SelectionItemPattern)selection).Select();
            if (!combo.TryGetCurrentPattern(SelectionPattern.Pattern, out object selected)
                || ((SelectionPattern)selected).Current.GetSelection() is not [var item]
                || item.Current.Name != name)
                throw new TimeoutException($"The measured window did not select {name} zoom.");
        }
        finally { dropdown.Collapse(); }
    }
}
