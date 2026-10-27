#nullable enable
using System;
using System.Windows;
using System.Windows.Input;

namespace KillerFamily.Shell
{
    /// <summary>Routes Ctrl+Q through the app's own close, save and active-operation safeguards.</summary>
    public static class QuitShortcut
    {
        public static RoutedCommand Command { get; } = new RoutedCommand("Quit", typeof(QuitShortcut));

        public static void Attach(Window window, Action requestQuit)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (requestQuit == null) throw new ArgumentNullException(nameof(requestQuit));
            window.InputBindings.Add(new KeyBinding(Command, Key.Q, ModifierKeys.Control));
            window.CommandBindings.Add(new CommandBinding(Command, (_, e) =>
            {
                e.Handled = true;
                requestQuit();
            }));
        }
    }
}
