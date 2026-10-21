using System.Collections.Generic;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class DocumentTextModelTests
    {
        private static DocumentTextModel Model(params string[] pages) =>
            new(pages.Length, page => pages[page]);

        [Fact]
        public void ReadsAcrossPagesWithASeparator()
        {
            var model = Model("Hello world", "Second page");

            Assert.Equal("Hello world\nSecond page", model.GetText(model.Start, model.End));
            Assert.Equal(new TextPosition(1, 0), model.Normalize(new TextPosition(0, 12)));
        }

        [Fact]
        public void LoadsOnlyThePagesItReaches()
        {
            var loaded = new HashSet<int>();
            var model = new DocumentTextModel(500, page => { loaded.Add(page); return "Page " + page; });

            var (start, end) = model.Enclosing(model.Start, ReadingUnit.Line);

            Assert.Equal("Page 0\n", model.GetText(start, end));
            Assert.DoesNotContain(250, loaded);
        }

        [Fact]
        public void WordsIncludeTheirTrailingSpaceAndLinesTheirNewline()
        {
            var model = Model("one two\nthree four");

            var word = model.Enclosing(new TextPosition(0, 5), ReadingUnit.Word);
            Assert.Equal("two\n", model.GetText(word.Start, word.End));
            var first = model.Enclosing(new TextPosition(0, 1), ReadingUnit.Word);
            Assert.Equal("one ", model.GetText(first.Start, first.End));
            var line = model.Enclosing(new TextPosition(0, 9), ReadingUnit.Line);
            Assert.Equal("three four", model.GetText(line.Start, line.End));
        }

        [Fact]
        public void MoveByLineCrossesPagesAndStopsAtTheEnd()
        {
            var model = Model("a\nb", "c");
            TextPosition start = model.Start, end = model.Start;
            model.Move(ref start, ref end, ReadingUnit.Line, 0);

            Assert.Equal(2, model.Move(ref start, ref end, ReadingUnit.Line, 5));
            Assert.Equal(new TextPosition(1, 0), start);
            Assert.Equal(start, end);   // a degenerate range stays degenerate
            Assert.Equal(-2, model.Move(ref start, ref end, ReadingUnit.Line, -9));
            Assert.Equal(model.Start, start);
        }

        [Fact]
        public void MoveByPageAndExpandToPage()
        {
            var model = Model("first", "second", "third");
            TextPosition start = new(0, 2), end = new(0, 3);

            Assert.Equal(1, model.Move(ref start, ref end, ReadingUnit.Page, 1));
            Assert.Equal("second\n", model.GetText(start, end));
        }

        [Fact]
        public void CharacterUnitsKeepSurrogatePairsTogether()
        {
            var model = Model("a😀b");
            TextPosition at = model.Start;

            model.MoveEndpoint(ref at, ReadingUnit.Character, 2);

            Assert.Equal(new TextPosition(0, 3), at);
        }

        [Fact]
        public void FindsTextAcrossAPageBreak()
        {
            var model = Model("total due", "now please");

            var found = model.Find(model.Start, model.End, "DUE\nNOW", backward: false, ignoreCase: true);

            Assert.NotNull(found);
            Assert.Equal(new TextPosition(0, 6), found!.Value.Start);
            Assert.Equal("due\nnow", model.GetText(found.Value.Start, found.Value.End));
        }

        [Fact]
        public void TaggedPagesReadInStructureOrderAndKeepUntaggedText()
        {
            // Visually: "Right" (MCID 1) sits left of "Left" (MCID 0); the tag tree says 0 then 1.
            var chars = new List<(string, int, int, int?)>
            {
                ("R", 0, 0, 1), ("i", 0, 0, 1),
                ("L", 1, 0, 0), ("e", 1, 0, 0),
                ("x", 2, 1, null)
            };

            PageReadingText page = PageReadingText.BuildTagged(chars, [0, 1]);

            Assert.Equal("Le\nRi\nx", page.Text);
            Assert.Equal([(2, 4)], page.Segments(0, 2));          // "Le" is carets 2..3 on the page
            Assert.Equal([(0, 4)], page.Segments(0, 5));          // both items cover carets 0..3
        }

        [Fact]
        public void EmptyDocumentIsOneEmptyRange()
        {
            var model = Model();
            Assert.Equal(model.Start, model.End);
            Assert.Equal(string.Empty, model.GetText(model.Start, model.End));
        }
    }
}
