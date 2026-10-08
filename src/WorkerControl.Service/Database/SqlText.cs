using System.Text;

namespace WorkerControl.Service.Database;

/// <summary>
/// The text of a statement without the values written in it. A statement that is running may
/// carry data of a table between its quotes, and none of that may leave the instance.
/// </summary>
internal static class SqlText
{
    public const int MaxLength = 4000;

    public static string Mask(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return "";

        var text = new StringBuilder(Math.Min(sql.Length, MaxLength));
        var i = 0;
        while (i < sql.Length && text.Length < MaxLength)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '\'' || ((c is 'N' or 'n') && next == '\'' && !IsWord(Last(text))))
            {
                i = SkipQuoted(sql, c == '\'' ? i : i + 1, '\'');
                text.Append("'?'");
            }
            else if (c == '[')
            {
                // A name, which is kept; a ] inside it is written twice.
                var end = SkipQuoted(sql, i, ']');
                text.Append(sql, i, end - i);
                i = end;
            }
            else if (c == '"')
            {
                var end = SkipQuoted(sql, i, '"');
                text.Append(sql, i, end - i);
                i = end;
            }
            else if (c == '-' && next == '-')
            {
                // What a comment says is anybody's guess.
                while (i < sql.Length && sql[i] != '\n')
                    i++;
            }
            else if (c == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                if (text.Length > 0 && Last(text) != ' ')
                    text.Append(' ');
            }
            else if (char.IsAsciiDigit(c) && !IsWord(Last(text)) && Last(text) != '@')
            {
                if (c == '0' && next is 'x' or 'X')
                    i += 2;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '.'))
                    i++;
                text.Append('?');
            }
            else if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && Last(text) != ' ')
                    text.Append(' ');
                i++;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }
        return text.ToString().Trim();
    }

    private static char Last(StringBuilder text) => text.Length == 0 ? ' ' : text[^1];

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '$';

    /// <summary>
    /// Where what opens at <paramref name="start"/> ends; the closing mark written twice stands for itself.
    /// </summary>
    private static int SkipQuoted(string sql, int start, char close)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }
}
