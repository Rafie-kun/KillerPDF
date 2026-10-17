using KillerPDF;
using Xunit;

namespace KillerPDF.Tests
{
    /// <summary>The title bar tool search's matching (Services/CommandSearch.cs).</summary>
    public class CommandSearchTests
    {
        private static int Score(string query, string text) => CommandSearch.Score(query, (text, 100));

        [Fact]
        public void NormalizeDropsCaseAccentsAndPunctuation()
        {
            Assert.Equal("exporter le pdf", CommandSearch.Normalize("Exportér le PDF..."));
            Assert.Equal("save as", CommandSearch.Normalize("  Save  As  "));
        }

        [Fact]
        public void EmptyQueryMatchesEverything()
        {
            Assert.Equal(0, Score("", "Redact"));
            Assert.Equal(0, Score("   ", "Redact"));
        }

        [Fact]
        public void UnrelatedTextDoesNotMatch()
        {
            Assert.Equal(CommandSearch.NoMatch, Score("zzz", "Redact"));
            Assert.Equal(CommandSearch.NoMatch, Score("print", "Redact"));
        }

        [Fact]
        public void TheStartOfTheNameBeatsAWordInsideIt()
        {
            Assert.True(Score("sig", "Signature") > Score("sig", "Digital Signature"));
            Assert.True(Score("red", "Redact") > Score("red", "Shared items"));
        }

        [Fact]
        public void AWordStartBeatsLettersInsideAWord()
        {
            Assert.True(Score("page", "Rotate Page CW") > Score("page", "Webpages"));
        }

        [Fact]
        public void InitialsLettersInOrderAndTyposStillMatch()
        {
            Assert.True(Score("fs", "Full screen") > 0);
            Assert.True(Score("hlt", "Highlight") > 0);
            Assert.True(Score("redcat", "Redact") > 0);
            Assert.True(Score("signatrue", "Signature") > 0);
        }

        [Fact]
        public void ScatteredLettersAndWrongFirstLettersDoNotMatch()
        {
            Assert.Equal(CommandSearch.NoMatch, Score("red", "Create bookmarks from headings"));
            Assert.Equal(CommandSearch.NoMatch, Score("fit", "find replace substitute"));
            Assert.Equal(CommandSearch.NoMatch, Score("night", "Move sidebar left / right"));
        }

        [Fact]
        public void EveryQueryWordHasToMatch()
        {
            Assert.True(Score("page rotate", "Rotate Page CW") > 0);
            Assert.Equal(CommandSearch.NoMatch, Score("page zebra", "Rotate Page CW"));
        }

        [Fact]
        public void AnAccentedNameMatchesAPlainQuery()
        {
            Assert.True(Score("schwarzen", "Schwärzen") > 0);
        }

        [Fact]
        public void TheBestFieldCountsAtItsWeight()
        {
            int byName = CommandSearch.Score("black", ("Black out", 100), ("censor", 70));
            int byKeyword = CommandSearch.Score("black", ("Redact", 100), ("black out censor", 70));
            Assert.True(byName > byKeyword);
            Assert.True(byKeyword > 0);
        }
    }
}
