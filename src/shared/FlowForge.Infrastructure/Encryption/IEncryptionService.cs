namespace FlowForge.Infrastructure.Encryption;

public interface IEncryptionService
{
    /// <summary>Encrypts a plaintext string. Returns an "enc:v1:..." prefixed ciphertext.</summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts an "enc:v1:..." ciphertext. If the value is not encrypted (legacy / plain text)
    /// it is returned as-is, so callers never need to check first.
    /// </summary>
    string Decrypt(string value);

    bool IsEncrypted(string value);
}
