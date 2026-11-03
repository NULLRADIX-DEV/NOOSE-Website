using System.Diagnostics.CodeAnalysis;

namespace NOOSE_Website.Infrastructure;

/// <summary>Ordered, case-sensitive NOOSE to FIB wording swaps of the data sweep; a test holds the demo literals against them.</summary>
public static class BrandSweep
{
    /// <summary>One plain, case-sensitive substring swap.</summary>
    public sealed record Replacement(string From, string To);

    private static readonly (string Word, string Result)[] Contracted =
    [
        ("bei", "beim"), ("von", "vom"), ("zu", "zum"), ("in", "im"), ("an", "am"),
    ];

    private static readonly (string Word, string Result)[] Dative =
    [
        ("mit", "mit dem"), ("aus", "aus dem"), ("nach", "nach dem"), ("seit", "seit dem"),
        ("außer", "außer dem"), ("gegenüber", "gegenüber dem"), ("auf", "auf dem"), ("hinter", "hinter dem"),
        ("neben", "neben dem"), ("über", "über dem"), ("unter", "unter dem"), ("vor", "vor dem"),
        ("zwischen", "zwischen dem"),
    ];

    /// <summary>Every swap in the order it runs; a row needs the sweep when it contains any <c>From</c>.</summary>
    public static IReadOnlyList<Replacement> Replacements { get; } = Build();

    /// <summary>Runs every swap in order; null and empty pass through.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var swap in Replacements)
        {
            text = text.Replace(swap.From, swap.To, StringComparison.Ordinal);
        }
        return text;
    }

    // the order is the grammar; a leading space stands in for a word boundary
    private static List<Replacement> Build()
    {
        var list = new List<Replacement>
        {
            // whole sentences
            new("in der Government Facility des National Office of Security Enforcement 5020, Los Santos, San Andreas",
                "im FIB-Tower, Los Santos, San Andreas"),
            new("Los Santos Police Department, ehemals Federal Investigation Bureau oder beim Department of Justice",
                "Los Santos Police Department oder beim Department of Justice"),

            // before article rules
            new("NOOSEI", "FIBI"),

            // compounds keep their article
            new("die NOOSE-", "die FIB-"), new("Die NOOSE-", "Die FIB-"),
            new("der NOOSE-", "der FIB-"), new("Der NOOSE-", "Der FIB-"),
        };

        foreach (var (word, result) in Contracted.Concat(Dative))
        {
            list.Add(new($" {word} der NOOSE", $" {result} FIB"));
            list.Add(new($"{Capitalise(word)} der NOOSE", $"{Capitalise(result)} FIB"));
        }
        list.Add(new(" zur NOOSE", " zum FIB"));
        list.Add(new("Zur NOOSE", "Zum FIB"));

        list.AddRange(
        [
            // space spares "oder"
            new(" der NOOSE", " des FIB"),
            new("die NOOSE", "das FIB"),
            new("Die NOOSE", "Das FIB"),

            new("National Office of Security Enforcement", "Federal Investigation Bureau"),
            new("N.O.O.S.E.", "F.I.B."),
            new("NOOSE", "FIB"),
            new("noose.info", "fibhq.com"),
        ]);
        return list;
    }

    private static string Capitalise(string word) => char.ToUpperInvariant(word[0]) + word[1..];
}
