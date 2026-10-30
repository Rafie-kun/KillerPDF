using System.Globalization;

namespace KillerPdf.Engine.Documents;

// The one reader of PDF date strings, shared by Document Info, the attachment reader and the signature
// reader so that a date form one of them accepts is accepted by all three.
internal static class PdfDateText
{
    // Reads D:YYYYMMDDHHmmSSOHH'mm, where everything after the year is optional. ISO 32000-1 7.9.4 lets
    // the offset drop its minutes (+01) and lets Z carry an HH'mm offset too (Adobe signers write
    // Z00'00'). Z means universal time either way, a trailing apostrophe is forgiven, and Z followed
    // by nothing but an apostrophe has an unambiguous time. Anything else is not a date.
    internal static bool TryParse(string? text, out DateTimeOffset date)
    {
        date = default;
        if (text is null || !text.StartsWith("D:", StringComparison.Ordinal) || text.Length < 6) return false;
        string digits = new([.. text.Skip(2).TakeWhile(char.IsAsciiDigit)]);
        if (digits.Length is not (4 or 6 or 8 or 10 or 12 or 14)) return false;
        int Part(int offset, int length, int fallback) => digits.Length >= offset + length
            ? int.Parse(digits.AsSpan(offset, length), CultureInfo.InvariantCulture) : fallback;
        string suffix = text[(2 + digits.Length)..];
        TimeSpan zone = TimeSpan.Zero;
        if (suffix.Length > 0 && suffix != "Z")
        {
            char sign = suffix[0];
            if (sign is not ('+' or '-' or 'Z')) return false;
            string compact = suffix[1..].Replace("'", string.Empty, StringComparison.Ordinal);
            if (!compact.All(char.IsAsciiDigit)) return false;
            // Z alone with its apostrophe is universal time; a sign always needs its hours.
            if (compact.Length == 0 && sign == 'Z') compact = "0000";
            if (compact.Length is not (2 or 4)) return false;
            int hours = int.Parse(compact.AsSpan(0, 2), CultureInfo.InvariantCulture);
            int minutes = compact.Length == 4 ? int.Parse(compact.AsSpan(2, 2), CultureInfo.InvariantCulture) : 0;
            if (sign != 'Z') zone = new TimeSpan(hours, minutes, 0) * (sign == '-' ? -1 : 1);
        }
        try
        {
            date = new DateTimeOffset(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1),
                Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0), zone);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
