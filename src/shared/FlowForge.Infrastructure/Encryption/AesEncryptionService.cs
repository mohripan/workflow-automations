using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowForge.Infrastructure.Encryption;

/// <summary>
/// AES-256-GCM encryption service.
/// The platform master key lives in config key "FlowForge:EncryptionKey" as a base64-encoded 32-byte value.
///
/// Generate a production key with:
///   dotnet script -e "Console.WriteLine(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));"
/// or in a C# REPL:
///   Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
///
/// DEV FALLBACK: if the key is not configured a fixed all-zeros key is used.
/// This must NEVER be used in production — the logger will warn loudly.
/// </summary>
public class AesEncryptionService : IEncryptionService
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;  // 96-bit nonce — recommended for GCM
    private const int TagSize   = 16;  // 128-bit auth tag

    // 32 zero-bytes in base64. Only for local dev — zero-key is trivially guessable.
    private const string DevFallbackKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly byte[] _key;

    public AesEncryptionService(IConfiguration config, ILogger<AesEncryptionService> logger)
    {
        var keyBase64 = config["FlowForge:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            logger.LogWarning(
                "FlowForge:EncryptionKey is not configured — using the insecure dev fallback key. " +
                "Set this env var to a base64-encoded 32-byte random value before going to production.");
            keyBase64 = DevFallbackKey;
        }

        _key = Convert.FromBase64String(keyBase64);
        if (_key.Length != 32)
            throw new InvalidOperationException(
                "FlowForge:EncryptionKey must decode to exactly 32 bytes (256 bits). " +
                "Generate one with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))");
    }

    public string Encrypt(string plaintext)
    {
        var nonce          = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext     = new byte[plaintextBytes.Length];
        var tag            = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: nonce (12) || ciphertext (n) || tag (16)
        var blob = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, NonceSize);
        tag.CopyTo(blob, NonceSize + ciphertext.Length);

        return Prefix + Convert.ToBase64String(blob);
    }

    public string Decrypt(string value)
    {
        if (!IsEncrypted(value)) return value; // plain-text pass-through for backwards compat

        var blob       = Convert.FromBase64String(value[Prefix.Length..]);
        var nonce      = blob[..NonceSize];
        var tag        = blob[^TagSize..];
        var ciphertext = blob[NonceSize..^TagSize];
        var plaintext  = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    public bool IsEncrypted(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal);
}
