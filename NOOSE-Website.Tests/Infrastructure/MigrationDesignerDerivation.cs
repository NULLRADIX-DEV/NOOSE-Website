using System.Text;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>A designer that has to keep its BuildTargetModel, with the migration and method that need it.</summary>
public sealed record DesignerNeed(string Designer, string Migration, string Method, string What);

/// <summary>
/// Finds the designers that migrations still need a model from, by reading the migration code (same rule as
/// scripts/designer-entleeren.sh): InsertData/UpdateData/DeleteData without columnTypes:, RenameColumn, RenameIndex and
/// any use of TargetModel need the model. In Up() that is the own designer, in Down() the one of the previous migration.
/// </summary>
public static partial class MigrationDesignerDerivation
{
    /// <param name="migrations">Migration id (file name without .cs) and code, sorted by id.</param>
    public static IReadOnlyList<DesignerNeed> Derive(IReadOnlyList<(string Id, string Code)> migrations)
    {
        var needs = new List<DesignerNeed>();
        for (var i = 0; i < migrations.Count; i++)
        {
            var (id, code) = migrations[i];
            var previous = i > 0 ? migrations[i - 1].Id : null;
            foreach (var (method, what) in FindHits(code))
            {
                // the first migration has no predecessor, so a hit in its Down() needs nothing
                var owner = method == "Down" ? previous : id;
                if (owner is not null)
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
        var (downStart, downEnd) = DownBody(masked);
        string MethodAt(int index) => index >= downStart && index < downEnd ? "Down" : "Up";

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

    private static (int Start, int End) DownBody(string masked)
    {
        var declaration = DownDeclaration().Match(masked);
        if (!declaration.Success)
        {
            return (0, 0);
        }

        var open = masked.IndexOf('{', declaration.Index);
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            if (masked[i] == '{')
            {
                depth++;
            }
            else if (masked[i] == '}' && --depth == 0)
            {
                return (open, i);
            }
        }

        return (open, masked.Length);
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

    /// <summary>Blanks out comments and the inside of string and char literals, so brackets in seed texts do not count.</summary>
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
            else if (c == '"' && string.CompareOrdinal(code, i, "\"\"\"", 0, 3) == 0)
            {
                var end = code.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end;
                for (i += 3; i < stop; i++)
                {
                    Blank(i);
                }

                i = Math.Min(i + 3, code.Length);
            }
            else if (c == '"')
            {
                var verbatim = (i > 0 && code[i - 1] == '@') || (i > 1 && code[i - 1] == '$' && code[i - 2] == '@');
                i = SkipLiteral(code, i + 1, '"', verbatim, Blank);
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

    /// <summary>Blanks a literal from just after its opening quote and returns the index after the closing quote.</summary>
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
                // an unterminated literal ends at the line, like in the script
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

    [GeneratedRegex(@"\bvoid\s+Down\s*\(")]
    private static partial Regex DownDeclaration();
}
