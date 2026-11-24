using System.Text;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>A designer a migration still needs.</summary>
public sealed record DesignerNeed(string Designer, string Migration, string Method, string What);

/// <summary>Finds the designers the migrations still need.</summary>
public static partial class MigrationDesignerDerivation
{
    /// <param name="migrations">Id and code per migration, sorted by id.</param>
    public static IReadOnlyList<DesignerNeed> Derive(IReadOnlyList<(string Id, string Code)> migrations)
    {
        var needs = new List<DesignerNeed>();
        for (var i = 0; i < migrations.Count; i++)
        {
            var (id, code) = migrations[i];
            var previous = i > 0 ? migrations[i - 1].Id : null;
            foreach (var (method, what) in FindHits(code))
            {
                string?[] owners = what == "TargetModel" || method == "Up" ? [id] : method == "Down" ? [previous] : [id, previous];
                // no predecessor
                foreach (var owner in owners.OfType<string>())
                {
                    needs.Add(new DesignerNeed(owner + ".Designer.cs", id, method, what));
                }
            }
        }

        return needs;
    }

    private static IEnumerable<(string Method, string What)> FindHits(string code)
    {
        var masked = Mask(code);
        var up = BodyOf(masked, UpDeclaration());
        var down = BodyOf(masked, DownDeclaration());
        string MethodAt(int index) =>
            index >= up.Start && index < up.End ? "Up" : index >= down.Start && index < down.End ? "Down" : "Helper";

        foreach (Match call in DataOrRenameCall().Matches(masked))
        {
            var open = call.Index + call.Length - 1;
            var close = MatchingParen(masked, open);
            var text = masked[open..close];
            if (call.Groups[1].Value is "RenameColumn" or "RenameIndex" || !ColumnTypesArgument().IsMatch(text))
            {
                yield return (MethodAt(call.Index), call.Groups[1].Value);
            }
        }

        foreach (Match use in TargetModelUse().Matches(masked))
        {
            yield return (MethodAt(use.Index), "TargetModel");
        }
    }

    private static (int Start, int End) BodyOf(string masked, Regex declaration)
    {
        var match = declaration.Match(masked);
        if (!match.Success)
        {
            return (0, 0);
        }

        var i = MatchingParen(masked, match.Index + match.Length - 1) + 1;
        while (i < masked.Length && char.IsWhiteSpace(masked[i]))
        {
            i++;
        }

        if (i < masked.Length && masked[i] == '{')
        {
            return (i, ScanEnd(masked, i, true));
        }

        return i < masked.Length && string.CompareOrdinal(masked, i, "=>", 0, 2) == 0 ? (i, ScanEnd(masked, i + 2, false)) : (0, 0);
    }

    private static int ScanEnd(string masked, int from, bool block)
    {
        var depth = 0;
        for (var i = from; i < masked.Length; i++)
        {
            var c = masked[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (--depth < 0 || (block && depth == 0))
                {
                    return i;
                }
            }
            else if (c == ';' && !block && depth == 0)
            {
                return i;
            }
        }

        return masked.Length;
    }

    private static int MatchingParen(string masked, int open)
    {
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            if (masked[i] == '(')
            {
                depth++;
            }
            else if (masked[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return masked.Length;
    }

    /// <summary>Blanks comments and literals.</summary>
    private static string Mask(string code)
    {
        var chars = code.ToCharArray();
        void Blank(int index)
        {
            if (chars[index] != '\n' && chars[index] != '\r')
            {
                chars[index] = ' ';
            }
        }

        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n')
                {
                    Blank(i++);
                }
            }
            else if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end + 2;
                while (i < stop)
                {
                    Blank(i++);
                }
            }
            else if (c == '"')
            {
                var verbatim = (i > 0 && code[i - 1] == '@') || (i > 1 && code[i - 1] == '$' && code[i - 2] == '@');
                var quotes = 1;
                while (i + quotes < code.Length && code[i + quotes] == '"')
                {
                    quotes++;
                }

                if (verbatim || quotes < 3)
                {
                    i = SkipLiteral(code, i + 1, '"', verbatim, Blank);
                    continue;
                }

                var end = code.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end;
                for (i += quotes; i < stop; i++)
                {
                    Blank(i);
                }

                i = Math.Min(i + quotes, code.Length);
            }
            else if (c == '\'')
            {
                i = SkipLiteral(code, i + 1, '\'', false, Blank);
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }

    /// <summary>Blanks one literal.</summary>
    private static int SkipLiteral(string code, int i, char quote, bool verbatim, Action<int> blank)
    {
        while (i < code.Length)
        {
            if (verbatim && code[i] == quote && i + 1 < code.Length && code[i + 1] == quote)
            {
                blank(i);
                blank(i + 1);
                i += 2;
            }
            else if (code[i] == quote)
            {
                return i + 1;
            }
            else if (!verbatim && code[i] == '\\' && i + 1 < code.Length)
            {
                blank(i);
                blank(i + 1);
                i += 2;
            }
            else if (!verbatim && code[i] == '\n')
            {
                // unterminated literal
                return i;
            }
            else
            {
                blank(i);
                i++;
            }
        }

        return i;
    }

    [GeneratedRegex(@"\b(InsertData|UpdateData|DeleteData|RenameColumn|RenameIndex)\s*\(")]
    private static partial Regex DataOrRenameCall();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])columnTypes\s*:")]
    private static partial Regex ColumnTypesArgument();

    [GeneratedRegex(@"\bTargetModel\b")]
    private static partial Regex TargetModelUse();

    [GeneratedRegex(@"\bvoid\s+Up\s*\(")]
    private static partial Regex UpDeclaration();

    [GeneratedRegex(@"\bvoid\s+Down\s*\(")]
    private static partial Regex DownDeclaration();
}
