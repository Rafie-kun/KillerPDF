using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using KillerPDF.Controls;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    // BF-U001: the page images get a Document element with the Text pattern over their text.
    public class DocumentSurfaceTests
    {
        private sealed class FakeSource(params string[][] pages) : IDocumentReadingSource
        {
            public (int, int, int, int)? Selected;
            public int ScrolledTo = -1;
            public int PageCount => pages.Length;
            public int CurrentPage { get; set; }
            public object Version => PageCount;
            public PageReadingText PageText(int page) => PageReadingText.Build(
                [.. pages[page].SelectMany((line, l) => line.Split(' ').SelectMany((word, w) =>
                    word.Select(c => (c.ToString(), l * 100 + w, l))))]);
            public IReadOnlyList<Rect> ScreenRects(int page, int startCaret, int endCaret) =>
                [new Rect(page * 1000 + startCaret, 0, endCaret - startCaret, 10)];
            public void Select(int startPage, int startCaret, int endPage, int endCaret) =>
                Selected = (startPage, startCaret, endPage, endCaret);
            public (int StartPage, int StartCaret, int EndPage, int EndCaret)? Selection => null;
            public void ScrollToPage(int page) => ScrolledTo = page;
            public event Action? TextChanged { add { } remove { } }
            public event Action? SelectionChanged { add { } remove { } }
        }

        [Fact]
        public void ExposesADocumentWithTheTextPattern() => OnSta(() =>
        {
            var source = new FakeSource(["Invoice total", "due now"], ["Page two"]);
            var surface = new DocumentSurface { Source = source };
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(surface);

            Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
            var text = Assert.IsAssignableFrom<ITextProvider>(peer.GetPattern(PatternInterface.Text));
            Assert.Equal("Invoice total\ndue now\nPage two", text.DocumentRange.GetText(-1));
        });

        [Fact]
        public void ReadsLineByLineAndPageByPage() => OnSta(() =>
        {
            var source = new FakeSource(["Invoice total", "due now"], ["Page two"]);
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = source }).GetPattern(PatternInterface.Text);

            ITextRangeProvider range = text.GetSelection()[0];
            range.ExpandToEnclosingUnit(TextUnit.Line);
            Assert.Equal("Invoice total\n", range.GetText(-1));
            Assert.Equal(1, range.Move(TextUnit.Line, 1));
            Assert.Equal("due now\n", range.GetText(-1));
            Assert.Equal(1, range.Move(TextUnit.Page, 1));
            Assert.Equal("Page two", range.GetText(-1));
            range.ScrollIntoView(true);
            Assert.Equal(1, source.ScrolledTo);
        });

        [Fact]
        public void SelectsAndOutlinesTheCharactersOfARange() => OnSta(() =>
        {
            var source = new FakeSource(["Invoice total"]);
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = source }).GetPattern(PatternInterface.Text);

            ITextRangeProvider word = text.DocumentRange.FindText("total", false, false)!;
            word.Select();
            double[] rects = word.GetBoundingRectangles();

            // "Invoice total": carets skip the inserted space, so "total" is carets 7..12.
            Assert.Equal((0, 7, 0, 12), source.Selected);
            Assert.Equal([7, 0, 5, 10], rects);
            Assert.Equal(true, word.GetAttributeValue(TextPattern.IsReadOnlyAttribute.Id));
        });

        private static void OnSta(Action test)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { test(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The STA test timed out.");
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
