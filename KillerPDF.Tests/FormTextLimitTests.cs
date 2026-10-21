using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class FormTextLimitTests
    {
        [Theory]
        [InlineData("abc", "", 3, true)]       // at the limit
        [InlineData("abcd", "abc", 3, false)]  // above it
        [InlineData("ab😀", "ab", 3, true)]    // a supplementary character counts once
        [InlineData("ab😀😀", "ab😀", 3, false)]
        [InlineData("abcde", "abcdef", 3, true)] // an overlong value may shrink
        [InlineData("abcdefg", "abcdef", 3, false)]
        [InlineData("anything", "", 0, true)]  // no limit
        public void AcceptsWhatTheEngineWillSave(string next, string previous, int maximum, bool accepted)
        {
            Assert.Equal(accepted, FormTextLimit.Accepts(next, previous, maximum));
        }

        [Fact]
        public void CountsRunesNotUtf16Units()
        {
            Assert.Equal(2, FormTextLimit.Count("a😀"));
            Assert.True(FormTextLimit.IsOver("abcd", 3));
            Assert.False(FormTextLimit.IsOver("ab😀", 3));
        }
    }
}
