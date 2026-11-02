using System.Text.RegularExpressions;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The QR encoder behind the wanted poster: a wrong bit is a code no phone reads, and nothing else would notice.</summary>
public class QrCodeTests
{
    private const string PosterUrl = "https://noose.info/gesucht/NOOSE-FA-2026-0042";

    /// <summary>The same address encoded by an independent encoder (QRCoder 1.7.0, level M), quiet zone removed.</summary>
    private static readonly string[] Reference =
    [
        "#######.#..####.#......##.#######",
        "#.....#......##.#.##.#....#.....#",
        "#.###.#..#....#..##.#####.#.###.#",
        "#.###.#.##......##...####.#.###.#",
        "#.###.#.#.###...#..#.#.##.#.###.#",
        "#.....#.##.....#.##.###.#.#.....#",
        "#######.#.#.#.#.#.#.#.#.#.#######",
        "........####..#..########........",
        "#...#.###.####.#.#..###.######..#",
        ".#####..##.#....##...#.#.#...####",
        "#...###...##.####.#.#..##....#.#.",
        "######..#.#.##.....##.#...#....#.",
        "##.#..#.#.#..#.##..#..#..#####...",
        "##.##..##.#.###...####.##....##..",
        ".#...###.#..####.##.####.#######.",
        "##.###.###....#####..#.#.####....",
        "#.#######.##.#...#.####...#.#.##.",
        ".###.#.#.#...#..#.#....#.#...###.",
        "#.##..#..###...#.#..######.#.#...",
        "#..#.#..####.....#####..#.##...#.",
        "....#.#..##.#..#..#.#..#.#####..#",
        "#.#..#..#.#.###.#..#.#####....##.",
        "..#.#.#.##....#.#..#...#.###...#.",
        ".....#..#...###..#######..####.#.",
        "####..#.....####.#..###.#####....",
        "........##.###.#.##.##..#...#.###",
        "#######.##.#..###.#.#...#.#.##.#.",
        "#.....#....##.##...##.#.#...#..##",
        "#.###.#.####.#..#.....#.######.##",
        "#.###.#...###.#..####..#..####.#.",
        "#.###.#..#.##..#.#..#...#.###....",
        "#.....#...###..###...##.###..#...",
        "#######.##.###..##.####.#.#..#..#",
    ];

    [Fact]
    public void ThePosterAddress_EncodesExactlyLikeAnIndependentEncoder()
    {
        var modules = QrCode.Encode(PosterUrl)!;

        Assert.Equal(Reference.Length, modules.GetLength(0));
        for (var y = 0; y < Reference.Length; y++)
        {
            var row = new string(Enumerable.Range(0, Reference.Length).Select(x => modules[y, x] ? '#' : '.').ToArray());
            Assert.True(Reference[y] == row, $"Zeile {y}: erwartet {Reference[y]}, bekommen {row}");
        }
    }

    [Theory]
    // capacities per the standard
    [InlineData(QrErrorCorrection.Low, 17, 21)]
    [InlineData(QrErrorCorrection.Low, 18, 25)]
    [InlineData(QrErrorCorrection.Medium, 14, 21)]
    [InlineData(QrErrorCorrection.Medium, 15, 25)]
    [InlineData(QrErrorCorrection.Quartile, 11, 21)]
    [InlineData(QrErrorCorrection.High, 7, 21)]
    [InlineData(QrErrorCorrection.High, 8, 25)]
    [InlineData(QrErrorCorrection.Low, 2953, 177)]
    [InlineData(QrErrorCorrection.Medium, 2331, 177)]
    [InlineData(QrErrorCorrection.Quartile, 1663, 177)]
    [InlineData(QrErrorCorrection.High, 1273, 177)]
    public void TheSmallestFittingVersionIsChosen(QrErrorCorrection level, int length, int size)
        => Assert.Equal(size, QrCode.Encode(new string('a', length), level)!.GetLength(0));

    [Theory]
    [InlineData(QrErrorCorrection.Low, 2954)]
    [InlineData(QrErrorCorrection.Medium, 2332)]
    [InlineData(QrErrorCorrection.Quartile, 1664)]
    [InlineData(QrErrorCorrection.High, 1274)]
    public void TextBeyondVersion40_GivesNoSymbol(QrErrorCorrection level, int length)
        => Assert.Null(QrCode.Encode(new string('a', length), level));

    [Fact]
    public void Umlauts_CountAsTheirUtf8Bytes()
    {
        // 14 UTF-8 bytes
        Assert.Equal(21, QrCode.Encode("ÄÖÜäöüß")!.GetLength(0));
        Assert.Equal(25, QrCode.Encode("ÄÖÜäöüßa")!.GetLength(0));
    }

    [Theory]
    // standard table, mask 0
    [InlineData(QrErrorCorrection.Low, 0b111011111000100)]
    [InlineData(QrErrorCorrection.Medium, 0b101010000010010)]
    [InlineData(QrErrorCorrection.Quartile, 0b011010101011111)]
    [InlineData(QrErrorCorrection.High, 0b001011010001001)]
    public void BothCopiesOfTheFormatBits_MatchTheStandard(QrErrorCorrection level, int expected)
    {
        var modules = QrCode.Encode(PosterUrl, level, mask: 0)!;
        var (first, second) = FormatCopies(modules);

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
    }

    [Fact]
    public void TheChosenMask_IsTheOneTheFormatBitsAnnounce()
    {
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
        {
            var auto = QrCode.Encode(PosterUrl, level)!;
            var (announced, _) = FormatCopies(auto);
            var mask = Enumerable.Range(0, 8).Single(m => FormatCopies(QrCode.Encode(PosterUrl, level, m)!).First == announced);

            Assert.True(Same(auto, QrCode.Encode(PosterUrl, level, mask)!), $"{level}: Maske {mask}");
        }
    }

    [Theory]
    // standard table
    [InlineData(QrErrorCorrection.Low, 150, 45, 0x07C94)]
    [InlineData(QrErrorCorrection.Low, 2953, 177, 0x28C69)]
    public void FromVersion7_BothVersionBlocksMatchTheStandard(QrErrorCorrection level, int length, int size, int expected)
    {
        var modules = QrCode.Encode(new string('a', length), level)!;
        Assert.Equal(size, modules.GetLength(0));

        int topRight = 0, bottomLeft = 0;
        for (var i = 0; i < 18; i++)
        {
            var a = size - 11 + i % 3;
            var b = i / 3;
            topRight |= (modules[b, a] ? 1 : 0) << i;
            bottomLeft |= (modules[a, b] ? 1 : 0) << i;
        }
        Assert.Equal(expected, topRight);
        Assert.Equal(expected, bottomLeft);
    }

    [Fact]
    public void FindersTimingAndDarkModule_SitWhereScannersLookForThem()
    {
        var modules = QrCode.Encode(PosterUrl)!;
        var size = modules.GetLength(0);

        foreach (var (row, column) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
        {
            for (var dy = 0; dy < 7; dy++)
            {
                for (var dx = 0; dx < 7; dx++)
                {
                    var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    Assert.Equal(ring != 2, modules[row + dy, column + dx]);
                }
            }
        }
        for (var i = 8; i < size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, modules[6, i]);
            Assert.Equal(i % 2 == 0, modules[i, 6]);
        }
        Assert.True(modules[size - 8, 8]);
    }

    [Fact]
    public void AMaskOutsideTheEight_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCode.Encode(PosterUrl, QrErrorCorrection.Medium, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCode.Encode(PosterUrl, QrErrorCorrection.Medium, -1));
    }

    [Fact]
    public void TheSvg_DrawsEveryDarkModuleInsideAQuietZone()
    {
        var modules = QrCode.Encode(PosterUrl)!;
        var size = modules.GetLength(0);

        var svg = QrCode.Svg(modules, "Steckbrief");

        var full = size + 2 * QrCode.QuietZone;
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg, StringComparison.Ordinal);
        Assert.Contains($"viewBox=\"0 0 {full} {full}\"", svg, StringComparison.Ordinal);

        // rasterise the runs
        var drawn = new bool[full, full];
        var path = Regex.Match(svg, "<path d=\"([^\"]+)\"").Groups[1].Value;
        foreach (Match run in Regex.Matches(path, @"M(\d+),(\d+)h(\d+)v1h-\d+z"))
        {
            var x = int.Parse(run.Groups[1].Value);
            var y = int.Parse(run.Groups[2].Value);
            for (var i = 0; i < int.Parse(run.Groups[3].Value); i++)
            {
                drawn[y, x + i] = true;
            }
        }
        for (var y = 0; y < full; y++)
        {
            for (var x = 0; x < full; x++)
            {
                var inside = y >= QrCode.QuietZone && y < size + QrCode.QuietZone
                    && x >= QrCode.QuietZone && x < size + QrCode.QuietZone;
                var expected = inside && modules[y - QrCode.QuietZone, x - QrCode.QuietZone];
                Assert.Equal(expected, drawn[y, x]);
            }
        }
    }

    [Fact]
    public void TheSvgLabel_IsEncoded()
    {
        var svg = QrCode.Svg(QrCode.Encode("a")!, "\"><script>alert(1)</script>");

        Assert.DoesNotContain("<script", svg, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"&quot;&gt;&lt;script&gt;", svg, StringComparison.Ordinal);
    }

    private static (int First, int Second) FormatCopies(bool[,] modules)
    {
        var size = modules.GetLength(0);
        int first = 0, second = 0;
        for (var i = 0; i <= 5; i++)
        {
            first |= Bit(modules[i, 8]) << i;
        }
        first |= Bit(modules[7, 8]) << 6;
        first |= Bit(modules[8, 8]) << 7;
        first |= Bit(modules[8, 7]) << 8;
        for (var i = 9; i < 15; i++)
        {
            first |= Bit(modules[8, 14 - i]) << i;
        }
        for (var i = 0; i < 8; i++)
        {
            second |= Bit(modules[8, size - 1 - i]) << i;
        }
        for (var i = 8; i < 15; i++)
        {
            second |= Bit(modules[size - 15 + i, 8]) << i;
        }
        return (first, second);
    }

    private static int Bit(bool dark) => dark ? 1 : 0;

    private static bool Same(bool[,] a, bool[,] b)
    {
        if (a.GetLength(0) != b.GetLength(0))
        {
            return false;
        }
        for (var y = 0; y < a.GetLength(0); y++)
        {
            for (var x = 0; x < a.GetLength(1); x++)
            {
                if (a[y, x] != b[y, x])
                {
                    return false;
                }
            }
        }
        return true;
    }
}
