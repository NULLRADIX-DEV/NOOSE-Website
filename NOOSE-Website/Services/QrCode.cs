using System.Globalization;
using System.Net;
using System.Text;

namespace NOOSE_Website.Services;

/// <summary>Error correction level of a QR code; a higher level survives more damage but needs a bigger symbol.</summary>
public enum QrErrorCorrection
{
    Low,
    Medium,
    Quartile,
    High,
}

/// <summary>QR encoder (byte mode, versions 1 to 40) to inline SVG; ported from Project Nayuki's QR Code generator, MIT, see THIRD-PARTY-NOTICES.md.</summary>
public static class QrCode
{
    /// <summary>Light modules around the symbol; scanners need four.</summary>
    public const int QuietZone = 4;

    private const int MaxVersion = 40;

    // [level][version], index 0 unused
    private static readonly int[][] EccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly int[][] BlockCount =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    /// <summary>The modules of the symbol for <paramref name="text"/>, [row, column], dark = true; null when it does not fit.</summary>
    /// <param name="mask">Forces one of the eight masks; unset picks the one with the lowest penalty.</param>
    public static bool[,]? Encode(string text, QrErrorCorrection level = QrErrorCorrection.Medium, int? mask = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (mask is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(mask));
        }

        var data = Encoding.UTF8.GetBytes(text);
        var version = 0;
        for (var v = 1; v <= MaxVersion; v++)
        {
            if (data.Length < 1 << CountBits(v) && 4 + CountBits(v) + data.Length * 8 <= DataCodewords(v, level) * 8)
            {
                version = v;
                break;
            }
        }
        if (version == 0)
        {
            return null;
        }

        var symbol = new Symbol(version);
        symbol.DrawFunctionPatterns();
        symbol.DrawCodewords(AddEccAndInterleave(DataBytes(data, version, level), version, level));

        var chosen = mask ?? symbol.BestMask(level);
        symbol.ApplyMask(chosen);
        symbol.DrawFormat(level, chosen);
        return symbol.Modules;
    }

    /// <summary>The symbol as a self-contained SVG with a white quiet zone; scales to whatever box it is put in.</summary>
    public static string Svg(bool[,] modules, string label)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var size = modules.GetLength(0);
        var full = size + 2 * QuietZone;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!modules[y, x])
                {
                    continue;
                }
                // one rect per run
                var start = x;
                while (x + 1 < size && modules[y, x + 1])
                {
                    x++;
                }
                var run = x - start + 1;
                path.Append(CultureInfo.InvariantCulture, $"M{start + QuietZone},{y + QuietZone}h{run}v1h-{run}z");
            }
        }
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {full} {full}\" shape-rendering=\"crispEdges\" "
            + $"role=\"img\" aria-label=\"{WebUtility.HtmlEncode(label)}\">"
            + $"<rect width=\"{full}\" height=\"{full}\" fill=\"#fff\"/><path d=\"{path}\" fill=\"#000\"/></svg>";
    }

    /// <summary>Character count field width of byte mode.</summary>
    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    /// <summary>Modules left for data and error correction once every function pattern is drawn.</summary>
    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }
        return result;
    }

    private static int DataCodewords(int version, QrErrorCorrection level)
        => RawDataModules(version) / 8 - EccPerBlock[(int)level][version] * BlockCount[(int)level][version];

    private static byte[] DataBytes(byte[] data, int version, QrErrorCorrection level)
    {
        var capacity = DataCodewords(version, level) * 8;
        var bits = new List<bool>(capacity);
        Append(bits, 0b0100, 4);
        Append(bits, data.Length, CountBits(version));
        foreach (var b in data)
        {
            Append(bits, b, 8);
        }
        Append(bits, 0, Math.Min(4, capacity - bits.Count));
        Append(bits, 0, (8 - bits.Count % 8) % 8);
        // pad 0xEC/0x11
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
        {
            Append(bits, pad, 8);
        }

        var result = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }
        return result;
    }

    private static void Append(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    private static byte[] AddEccAndInterleave(byte[] data, int version, QrErrorCorrection level)
    {
        var blocks = BlockCount[(int)level][version];
        var eccLength = EccPerBlock[(int)level][version];
        var raw = RawDataModules(version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLength = raw / blocks;
        var divisor = ReedSolomonDivisor(eccLength);

        var all = new byte[blocks][];
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            var chunk = data.AsSpan(k, length).ToArray();
            k += length;
            var block = new byte[shortLength + 1];
            chunk.CopyTo(block, 0);
            ReedSolomonRemainder(chunk, divisor).CopyTo(block, block.Length - eccLength);
            all[i] = block;
        }

        var result = new byte[raw];
        for (int i = 0, k = 0; i < all[0].Length; i++)
        {
            for (var j = 0; j < blocks; j++)
            {
                // skip short-block padding
                if (i != shortLength - eccLength || j >= shortBlocks)
                {
                    result[k++] = all[j][i];
                }
            }
        }
        return result;
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < result.Length; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < result.Length)
                {
                    result[j] ^= result[j + 1];
                }
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= (byte)Multiply(divisor[i], factor);
            }
        }
        return result;
    }

    // GF(256) multiply
    private static int Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    /// <summary>The 15 format bits (level and mask, BCH-protected and masked) as drawn next to the finders.</summary>
    private static int FormatBits(QrErrorCorrection level, int mask)
    {
        var levelBits = level switch
        {
            QrErrorCorrection.Low => 1,
            QrErrorCorrection.Medium => 0,
            QrErrorCorrection.Quartile => 3,
            _ => 2,
        };
        var data = levelBits << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }
        return (data << 10 | rem) ^ 0x5412;
    }

    /// <summary>The 18 version bits drawn twice from version 7 on.</summary>
    private static int VersionBits(int version)
    {
        var rem = version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }
        return version << 12 | rem;
    }

    private sealed class Symbol(int version)
    {
        private readonly int _size = version * 4 + 17;
        private readonly bool[,] _function = new bool[version * 4 + 17, version * 4 + 17];

        public bool[,] Modules { get; } = new bool[version * 4 + 17, version * 4 + 17];

        public void DrawFunctionPatterns()
        {
            for (var i = 0; i < _size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }

            Finder(3, 3);
            Finder(_size - 4, 3);
            Finder(3, _size - 4);

            var positions = AlignmentPositions();
            var count = positions.Length;
            for (var i = 0; i < count; i++)
            {
                for (var j = 0; j < count; j++)
                {
                    // finder corners
                    if (!(i == 0 && j == 0 || i == 0 && j == count - 1 || i == count - 1 && j == 0))
                    {
                        Alignment(positions[i], positions[j]);
                    }
                }
            }

            // reserve format area
            DrawFormat(QrErrorCorrection.Medium, 0);
            DrawVersion();
        }

        public void DrawCodewords(byte[] codewords)
        {
            var i = 0;
            for (var right = _size - 1; right >= 1; right -= 2)
            {
                // skip timing column
                if (right == 6)
                {
                    right = 5;
                }
                for (var vert = 0; vert < _size; vert++)
                {
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? _size - 1 - vert : vert;
                        if (!_function[y, x] && i < codewords.Length * 8)
                        {
                            Modules[y, x] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        public int BestMask(QrErrorCorrection level)
        {
            var best = 0;
            var lowest = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormat(level, mask);
                var penalty = Penalty();
                if (penalty < lowest)
                {
                    best = mask;
                    lowest = penalty;
                }
                // undo mask
                ApplyMask(mask);
            }
            return best;
        }

        public void ApplyMask(int mask)
        {
            for (var y = 0; y < _size; y++)
            {
                for (var x = 0; x < _size; x++)
                {
                    var invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    };
                    if (invert && !_function[y, x])
                    {
                        Modules[y, x] ^= true;
                    }
                }
            }
        }

        public void DrawFormat(QrErrorCorrection level, int mask)
        {
            var bits = FormatBits(level, mask);
            for (var i = 0; i <= 5; i++)
            {
                Set(8, i, Bit(bits, i));
            }
            Set(8, 7, Bit(bits, 6));
            Set(8, 8, Bit(bits, 7));
            Set(7, 8, Bit(bits, 8));
            for (var i = 9; i < 15; i++)
            {
                Set(14 - i, 8, Bit(bits, i));
            }

            for (var i = 0; i < 8; i++)
            {
                Set(_size - 1 - i, 8, Bit(bits, i));
            }
            for (var i = 8; i < 15; i++)
            {
                Set(8, _size - 15 + i, Bit(bits, i));
            }
            // always dark
            Set(8, _size - 8, true);
        }

        private void DrawVersion()
        {
            if (version < 7)
            {
                return;
            }
            var bits = VersionBits(version);
            for (var i = 0; i < 18; i++)
            {
                var bit = Bit(bits, i);
                var a = _size - 11 + i % 3;
                var b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }

        private void Finder(int x, int y)
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    var xx = x + dx;
                    var yy = y + dy;
                    if (xx >= 0 && xx < _size && yy >= 0 && yy < _size)
                    {
                        Set(xx, yy, distance != 2 && distance != 4);
                    }
                }
            }
        }

        private void Alignment(int x, int y)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    Set(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
        }

        private int[] AlignmentPositions()
        {
            if (version == 1)
            {
                return [];
            }
            var count = version / 7 + 2;
            var step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var result = new int[count];
            result[0] = 6;
            for (int i = count - 1, position = _size - 7; i >= 1; i--, position -= step)
            {
                result[i] = position;
            }
            return result;
        }

        // x column, y row
        private void Set(int x, int y, bool dark)
        {
            Modules[y, x] = dark;
            _function[y, x] = true;
        }

        private int Penalty()
        {
            var result = 0;
            for (var y = 0; y < _size; y++)
            {
                result += LinePenalty(i => Modules[y, i]);
            }
            for (var x = 0; x < _size; x++)
            {
                result += LinePenalty(i => Modules[i, x]);
            }

            for (var y = 0; y < _size - 1; y++)
            {
                for (var x = 0; x < _size - 1; x++)
                {
                    var colour = Modules[y, x];
                    if (colour == Modules[y, x + 1] && colour == Modules[y + 1, x] && colour == Modules[y + 1, x + 1])
                    {
                        result += 3;
                    }
                }
            }

            var dark = 0;
            foreach (var module in Modules)
            {
                if (module)
                {
                    dark++;
                }
            }
            var total = _size * _size;
            var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            return result + k * 10;
        }

        // runs and finder lookalikes
        private int LinePenalty(Func<int, bool> at)
        {
            var result = 0;
            var runColour = false;
            var run = 0;
            var history = new int[7];
            for (var i = 0; i < _size; i++)
            {
                if (at(i) == runColour)
                {
                    run++;
                    if (run == 5)
                    {
                        result += 3;
                    }
                    else if (run > 5)
                    {
                        result++;
                    }
                }
                else
                {
                    AddHistory(run, history);
                    if (!runColour)
                    {
                        result += FinderLike(history) * 40;
                    }
                    runColour = at(i);
                    run = 1;
                }
            }
            if (runColour)
            {
                AddHistory(run, history);
                run = 0;
            }
            run += _size;
            AddHistory(run, history);
            return result + FinderLike(history) * 40;
        }

        private void AddHistory(int run, int[] history)
        {
            // count leading border
            if (history[0] == 0)
            {
                run += _size;
            }
            Array.Copy(history, 0, history, 1, history.Length - 1);
            history[0] = run;
        }

        private static int FinderLike(int[] history)
        {
            var n = history[1];
            var core = n > 0 && history[2] == n && history[3] == n * 3 && history[4] == n && history[5] == n;
            return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0)
                + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
        }

        private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
    }
}
