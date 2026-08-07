using System.Security.Cryptography;
using System.Text;

namespace McpPrivateLibrary.Auth;

/// <summary>
/// Generation, parsing and verification of API key tokens.
///
/// Wire format: <c>mcpl_&lt;keyId&gt;_&lt;secret&gt;</c>
///   - <c>mcpl_</c>  a fixed, greppable prefix. Makes a leaked key recognisable in logs, git
///                   history and secret scanners, and lets the auth handler reject junk before
///                   touching the database.
///   - <c>keyId</c>  16 chars of base32hex-ish alphabet. Public, indexed, non-secret. Carrying it
///                   in the token turns verification into a single indexed lookup instead of
///                   hashing the presented value against every stored key.
///   - <c>secret</c> 43 chars encoding 256 bits of CSPRNG output. Only its SHA-256 is persisted.
///
/// SHA-256 (not a password KDF like bcrypt/argon2) is the right primitive here: the secret is
/// full-entropy machine-generated, so there is no dictionary to attack and a slow KDF would only
/// tax every request. That's the same reasoning GitHub/Stripe-style tokens use.
/// </summary>
public static class ApiKeyToken
{
    public const string Prefix = "mcpl_";

    /// <summary>Authorization scheme name: <c>Authorization: ApiKey mcpl_...</c>.</summary>
    public const string Scheme = "ApiKey";

    private const int KeyIdBytes = 10;   // -> 16 base32 chars
    private const int SecretBytes = 32;  // 256 bits -> 43 base64url chars

    /// <summary>
    /// Crockford-style base32 alphabet for the key id: unambiguous when read aloud or
    /// transcribed, and safe inside a token whose parts are separated by '_'.
    /// </summary>
    private const string Base32Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>A freshly generated key: the plaintext to hand back once, plus what to persist.</summary>
    public readonly record struct Generated(string Token, string KeyId, string SecretHash);

    public static Generated Generate()
    {
        var keyId = RandomBase32(KeyIdBytes);
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        return new Generated($"{Prefix}{keyId}_{secret}", keyId, HashSecret(secret));
    }

    /// <summary>
    /// Splits a presented token into its public id and secret halves. Returns false for anything
    /// that isn't shaped like one of our tokens, so malformed input never reaches the database.
    /// </summary>
    public static bool TryParse(string? token, out string keyId, out string secret)
    {
        keyId = "";
        secret = "";
        if (string.IsNullOrWhiteSpace(token)) return false;

        var span = token.AsSpan().Trim();
        if (!span.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var body = span[Prefix.Length..];
        var sep = body.IndexOf('_');
        if (sep <= 0 || sep == body.Length - 1) return false;

        keyId = body[..sep].ToString();
        secret = body[(sep + 1)..].ToString();
        return keyId.Length > 0 && secret.Length > 0;
    }

    public static string HashSecret(string secret) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Constant-time comparison of the presented secret's hash against the stored one, so response
    /// timing can't be used to recover a valid secret byte by byte.
    /// </summary>
    public static bool VerifySecret(string presentedSecret, string storedHash)
    {
        var presented = Encoding.UTF8.GetBytes(HashSecret(presentedSecret));
        var stored = Encoding.UTF8.GetBytes(storedHash ?? "");
        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }

    /// <summary>
    /// Non-secret display form for the UI/API: the prefix and key id only. Enough to tell two keys
    /// apart and match one against a client config, while revealing nothing usable.
    /// </summary>
    public static string DisplayPrefix(string keyId) => $"{Prefix}{keyId}…";

    private static string RandomBase32(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        var sb = new StringBuilder(byteCount * 8 / 5 + 1);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
