using System.Security.Cryptography;
using System.Text;

namespace NOOSE_Website.Services;

/// <summary>The secret in a calendar feed link: 256 random bits, only its SHA-256 is stored, compared in constant time.</summary>
public static class CalendarFeedToken
{
    public const int ByteLength = 32;

    /// <summary>Length of the URL-safe base64 form of <see cref="ByteLength"/> bytes, without padding.</summary>
    public const int TextLength = 43;

    /// <summary>A fresh secret, URL-safe base64 without padding.</summary>
    public static string Generate()
        => Base64Url(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>Lowercase hex SHA-256 of the secret, the only form that is stored.</summary>
    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>True when the text has the exact shape of a generated secret; anything else cannot match a stored hash.</summary>
    public static bool HasValidShape(string? token)
        => token is { Length: TextLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Constant-time comparison of a presented secret against a stored hash.</summary>
    public static bool Matches(string? storedHash, string? token)
    {
        if (string.IsNullOrEmpty(storedHash) || !HasValidShape(token))
        {
            return false;
        }
        var stored = Encoding.ASCII.GetBytes(storedHash);
        var presented = Encoding.ASCII.GetBytes(Hash(token!));
        return CryptographicOperations.FixedTimeEquals(stored, presented);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
