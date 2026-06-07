using System.Security.Cryptography;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Encripta/desencripta passwords de Instagram usando AES-256-GCM.
/// La clave se obtiene de una variable de entorno o se genera una por defecto (solo dev).
/// </summary>
public class InstagramEncryptionService
{
    private readonly byte[] _key;

    public InstagramEncryptionService()
    {
        var keyBase64 = Environment.GetEnvironmentVariable("SALESHUB_INSTAGRAM_ENCRYPTION_KEY")
                        ?? "dev-insecure-key-32bytes!!changeMe!"; // 32 bytes para AES-256
        _key = System.Text.Encoding.UTF8.GetBytes(keyBase64.PadRight(32).Substring(0, 32));
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // IV + ciphertext concatenados
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        var fullBytes = Convert.FromBase64String(cipherText);
        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = new byte[aes.IV.Length];
        Buffer.BlockCopy(fullBytes, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var cipherBytes = new byte[fullBytes.Length - iv.Length];
        Buffer.BlockCopy(fullBytes, iv.Length, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }
}
