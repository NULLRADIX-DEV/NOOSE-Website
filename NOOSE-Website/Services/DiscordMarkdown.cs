using System.Text;

namespace NOOSE_Website.Services;

/// <summary>Makes free text safe to put into Discord markdown.</summary>
public static class DiscordMarkdown
{
    /// <summary>Text that cannot switch on formatting or break out of a link; at most <paramref name="maxChars"/> long, ellipsis included.</summary>
    public static string Escape(string text, int maxChars = int.MaxValue)
    {
        var sb = new StringBuilder(text.Length);
        // room for ellipsis
        var fit = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            // swapped, not escaped
            if (c is '[' or ']')
            {
                sb.Append(c == '[' ? '［' : '］');
            }
            else
            {
                if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '(' or ')' or '<' or '#')
                {
                    sb.Append('\\');
                }
                sb.Append(c);
                // keep emoji whole
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(text[++i]);
                }
            }
            if (sb.Length < maxChars)
            {
                fit = sb.Length;
            }
        }
        return sb.Length <= maxChars ? sb.ToString() : sb.ToString(0, fit) + "…";
    }
}
