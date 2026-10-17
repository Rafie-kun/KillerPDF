using System.Globalization;
using System.Text;

namespace KillerPDF
{
    /// <summary>
    /// Fuzzy matching for the title bar's tool search. Plain text in and a score out, free of WPF so
    /// KillerPDF.Tests can link this file the way it links ShortcutTable.cs. A higher score is a
    /// better match; <see cref="NoMatch"/> means the text does not match at all.
    ///
    /// Each word of the query has to match somewhere in the text, as the start of the text, the
    /// start of a word, anywhere inside, as the words' initials, as letters in order, or with one
    /// typing slip, and those kinds of match rank in that order. So "red" finds Redact first, "fs"
    /// finds Full screen, "hlt" finds Highlight and "redcat" still finds Redact.
    /// </summary>
    internal static class CommandSearch
    {
        internal const int NoMatch = -1;

        /// <summary>
        /// Lower case without accents, with every run of characters that are not letters or digits
        /// turned into one space, so "Exportér le PDF..." compares as "exporter le pdf".
        /// </summary>
        internal static string Normalize(string text)
        {
            var normalized = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char character in text.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(character))
                {
                    if (pendingSpace && normalized.Length > 0) normalized.Append(' ');
                    normalized.Append(char.ToLowerInvariant(character));
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = true;
                }
            }
            return normalized.ToString();
        }

        /// <summary>
        /// The best score of <paramref name="query"/> over several texts, each counted at its weight
        /// in percent: a command's own name at 100, its English name and its keywords lower.
        /// An empty query matches everything with a score of 0.
        /// </summary>
        internal static int Score(string query, params (string Text, int Weight)[] fields)
        {
            string normalizedQuery = Normalize(query);
            if (normalizedQuery.Length == 0) return 0;
            int best = NoMatch;
            foreach (var (text, weight) in fields)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                int score = ScoreText(normalizedQuery, Normalize(text));
                if (score > 0) best = Math.Max(best, Math.Max(1, score * weight / 100));
            }
            return best;
        }

        /// <summary>Scores an already normalized query against already normalized text.</summary>
        internal static int ScoreText(string query, string text)
        {
            if (query.Length == 0 || text.Length == 0) return NoMatch;
            string[] words = text.Split(' ');
            int total = 0;
            foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int score = ScoreToken(token, text, words);
                if (score == NoMatch) return NoMatch;
                total += score;
            }
            // The whole query reading as the start of the text beats the same words found apart.
            if (text.StartsWith(query, StringComparison.Ordinal)) total += 200;
            return total;
        }

        private static int ScoreToken(string token, string text, string[] words)
        {
            if (text.StartsWith(token, StringComparison.Ordinal))
                return 900 - Math.Min(200, text.Length - token.Length);
            for (int i = 0; i < words.Length; i++)
                if (words[i].StartsWith(token, StringComparison.Ordinal))
                    return 800 - Math.Min(100, i * 10);
            int index = text.IndexOf(token, StringComparison.Ordinal);
            if (index >= 0) return 600 - Math.Min(150, index);
            if (token.Length >= 2 && words.Length >= 2 && Initials(words).StartsWith(token, StringComparison.Ordinal))
                return 550;
            int inOrder = LettersInOrder(token, text);
            if (inOrder != NoMatch) return inOrder;
            if (token.Length >= 4)
                foreach (string word in words)
                    if (WithinOneSlip(token, word)) return 250;
            return NoMatch;
        }

        private static string Initials(string[] words)
        {
            var initials = new StringBuilder(words.Length);
            foreach (string word in words)
                if (word.Length > 0) initials.Append(word[0]);
            return initials.ToString();
        }

        // The query's letters appear in order, starting at the start of a word. Adjacent letters
        // and letters that start words score up and gaps score down; a match spread thinner than
        // two skipped letters per query letter does not count, so "red" does not find
        // "cReate bookmarks from heaDings".
        private static int LettersInOrder(string token, string text)
        {
            if (token.Length < 2) return NoMatch;
            int best = NoMatch;
            for (int start = 0; start < text.Length; start++)
            {
                if (text[start] != token[0] || (start > 0 && text[start - 1] != ' ')) continue;
                int matched = 1, last = start, run = 0, gaps = 0, score = 325;
                for (int i = start + 1; i < text.Length && matched < token.Length; i++)
                {
                    if (text[i] != token[matched]) continue;
                    int gap = i - last - 1;
                    gaps += gap;
                    score -= gap * 6;
                    run = gap == 0 ? run + 1 : 0;
                    score += run * 12;
                    if (text[i - 1] == ' ') score += 25;
                    last = i;
                    matched++;
                }
                if (matched < token.Length || gaps > token.Length * 2) continue;
                best = Math.Max(best, Math.Min(score, 440));
            }
            return best >= 120 ? best : NoMatch;
        }

        // One letter missing, extra, wrong or swapped with its neighbor, against the start of the
        // word that is the query's length or one letter either side of it. The first letter has to
        // be right, so "night" does not find "right".
        private static bool WithinOneSlip(string token, string word)
        {
            if (word.Length == 0 || word[0] != token[0]) return false;
            for (int length = token.Length - 1; length <= token.Length + 1; length++)
            {
                if (length < 1 || length > word.Length) continue;
                if (EditDistance(token, word[..length]) <= 1) return true;
            }
            return false;
        }

        // Optimal string alignment distance: insertions, deletions, substitutions and adjacent swaps.
        private static int EditDistance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
            return d[a.Length, b.Length];
        }
    }
}
