using FlowForge.Infrastructure.Encryption;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowForge.Integration.Tests.Unit;

/// <summary>
/// Pure unit tests for AesEncryptionService — no containers required.
/// </summary>
public class AesEncryptionServiceTests
{
    // A known 32-byte key (base64) for deterministic test setup
    private const string TestKeyBase64 = "dGVzdGtleXRlc3RrZXl0ZXN0a2V5dGVzdGtleXRlc3Q="; // "testkeytestkeytestkeytestkeytes" padded

    private static AesEncryptionService BuildService(string? keyBase64 = TestKeyBase64)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(keyBase64 is null
                ? []
                : [new KeyValuePair<string, string?>("FlowForge:EncryptionKey", keyBase64)])
            .Build();
        return new AesEncryptionService(config, NullLogger<AesEncryptionService>.Instance);
    }

    [Fact]
    public void Encrypt_ThenDecrypt_ReturnsOriginalPlaintext()
    {
        var sut = BuildService();
        var plaintext = "Host=db.example.com;Database=erp;Username=sa;Password=s3cr3t!";

        var encrypted = sut.Encrypt(plaintext);
        var decrypted = sut.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesEncV1Prefix()
    {
        var sut = BuildService();

        var encrypted = sut.Encrypt("any-secret");

        encrypted.Should().StartWith("enc:v1:");
    }

    [Fact]
    public void Encrypt_SamePlaintextTwice_ProducesDifferentCiphertexts()
    {
        // Each call uses a fresh random nonce, so ciphertexts must differ
        var sut = BuildService();
        var plaintext = "same-connection-string";

        var first = sut.Encrypt(plaintext);
        var second = sut.Encrypt(plaintext);

        first.Should().NotBe(second, "each encryption uses a unique nonce");
    }

    [Fact]
    public void IsEncrypted_EncryptedValue_ReturnsTrue()
    {
        var sut = BuildService();
        var encrypted = sut.Encrypt("secret");

        sut.IsEncrypted(encrypted).Should().BeTrue();
    }

    [Fact]
    public void IsEncrypted_Plaintext_ReturnsFalse()
    {
        var sut = BuildService();

        sut.IsEncrypted("Host=plain;Password=plain").Should().BeFalse();
    }

    [Fact]
    public void Decrypt_Plaintext_PassesThroughUnchanged()
    {
        // Backwards-compat: values stored before encryption was introduced must still work
        var sut = BuildService();
        var plain = "not-yet-encrypted-value";

        sut.Decrypt(plain).Should().Be(plain);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var sut = BuildService();
        var encrypted = sut.Encrypt("tamper-me");

        // Flip a byte in the base64 payload to simulate tampering
        var prefix = "enc:v1:";
        var blob = Convert.FromBase64String(encrypted[prefix.Length..]);
        blob[^1] ^= 0xFF; // flip last byte (inside the auth tag)
        var tampered = prefix + Convert.ToBase64String(blob);

        var act = () => sut.Decrypt(tampered);
        act.Should().Throw<System.Security.Cryptography.CryptographicException>(
            "AES-GCM authentication tag verification must fail on tampered ciphertext");
    }

    [Fact]
    public void Constructor_MissingKey_UsesDevFallbackAndDoesNotThrow()
    {
        // Dev fallback (all-zeros key) should be usable — just logs a warning
        var act = () => BuildService(keyBase64: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_InvalidKeyLength_ThrowsInvalidOperationException()
    {
        // A key that is not exactly 32 bytes must be rejected
        var shortKey = Convert.ToBase64String(new byte[16]); // 16 bytes, not 32

        var act = () => BuildService(keyBase64: shortKey);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32 bytes*");
    }
}
