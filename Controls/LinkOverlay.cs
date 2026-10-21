using System;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The hit area over a PDF link on the page. Besides the mouse, it is a Tab stop that Enter
    /// follows, and a Hyperlink with the Invoke pattern for screen readers (BF-U001).
    /// </summary>
    internal sealed class LinkOverlay : Canvas
    {
        private readonly Action _follow;

        internal LinkOverlay(Action follow)
        {
            _follow = follow;
            Focusable = true;
            KeyboardNavigation.SetIsTabStop(this, true);
        }

        internal void Follow() => _follow();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Key != Key.Enter) return;
            Follow();
            e.Handled = true;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(LinkOverlay owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Hyperlink;
            protected override string GetClassNameCore() => "PdfLink";
            protected override bool IsControlElementCore() => true;
            protected override bool IsKeyboardFocusableCore() => true;

            protected override string GetNameCore()
            {
                string name = base.GetNameCore();
                return string.IsNullOrEmpty(name) ? Owner.GetValue(ToolTipProperty) as string ?? string.Empty : name;
            }

            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

            public void Invoke() => ((LinkOverlay)Owner).Follow();
        }
    }
}
