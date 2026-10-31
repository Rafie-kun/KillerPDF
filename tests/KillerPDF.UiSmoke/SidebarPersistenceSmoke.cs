using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class SidebarPersistenceSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static int Run(Window owner, string folder, string phase) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        Pump();
        owner.Opacity = 1; owner.Activate();
        if (phase == "empty")
        {
            if (owner.GetType().GetProperty("_doc", Members)!.GetValue(owner) is not null)
                throw new InvalidOperationException("Empty-window check restored a document unexpectedly.");
            Wait(() => Collapsed(owner), "empty window auto-collapsed");
            Console.WriteLine("PASS empty-window automatic collapse is isolated from the saved preference.");
            return 0;
        }
        string source = Path.Combine(folder, "sidebar.pdf");
        File.WriteAllBytes(source, CliReplacementSmoke.Fixture("BT /F1 14 Tf 20 250 Td (Sidebar preference) Tj ET"));
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "sidebar document rendered");
        Pump();
        if (phase is "closed" or "expand-key")
        {
            if (!Collapsed(owner)) throw new InvalidOperationException("A restarted process reopened the deliberately closed sidebar.");
        }
        else if (phase == "open" && Collapsed(owner))
            throw new InvalidOperationException("A restarted process lost the deliberately expanded sidebar preference.");

        if (phase is "close-arrow" or "close-key")
        {
            if (Collapsed(owner)) Toggle(owner);
            Wait(() => !Collapsed(owner), "sidebar expanded before explicit close");
            if (phase == "close-key") Key(owner); else Toggle(owner);
            Wait(() => Collapsed(owner), "explicit sidebar close completed");
        }
        else if (phase == "expand-key")
        {
            Key(owner);
            Wait(() => !Collapsed(owner), "explicit sidebar expansion completed");
        }
        Console.WriteLine($"PASS sidebar phase {phase}: collapsed={Collapsed(owner)}.");
        return 0;
    });

    private static bool Collapsed(Window owner) => (bool)owner.GetType().GetField("_sidebarCollapsed", Members)!.GetValue(owner)!
        && ((Border)owner.FindName("SidebarBorder")).Visibility == Visibility.Collapsed;
    private static void Toggle(Window owner) => ((Button)owner.FindName("SidebarToggleBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Key(Window owner)
    {
        ShortcutHintsSmoke.Focus(owner);
        ShortcutHintsSmoke.Key(owner, 0x78);
        Pump();
    }
}
