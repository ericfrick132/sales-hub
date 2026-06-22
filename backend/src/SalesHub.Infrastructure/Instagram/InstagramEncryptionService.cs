using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Cifra/descifra passwords de Instagram con AES. La clave sale de config
/// (<c>Instagram:EncryptionKey</c> o la env var <c>SALESHUB_INSTAGRAM_ENCRYPTION_KEY</c>);
/// si no hay ninguna, usa una clave de dev hardcodeada (la MISMA en todos lados, así
/// no rompe entre entornos que no tengan el secret configurado).
///
/// Diseño tolerante a "no hay secret en todos lados":
/// - El descifrado NUNCA tira excepción: si el valor guardado no se puede descifrar con
///   la clave actual (clave distinta, o el valor es texto plano de una cuenta importada
///   sin cifrar), se devuelve tal cual en vez de crashear el login batch.
/// - El cifrado degrada a texto plano si por algún motivo falla, para no perder la cuenta.
/// </summary>
public class InstagramEncryptionService
{
    // 32 bytes para AES-256. Hardcodeada → "disponible en todos lados" por definición.
    private const string DevKey = "dev-insecure-key-32bytes!!changeMe!";
    private const string EncPrefix = "enc:v1:";

    private readonly byte[] _key;
    private readonly bool _hasRealKey;
    private readonly ILogger<InstagramEncryptionService> _log;

    public InstagramEncryptionService(IConfiguration config, ILogger<InstagramEncryptionService> log)
    {
        _log = log;

        var configured = config["Instagram:EncryptionKey"]
                         ?? Environment.GetEnvironmentVariable("SALESHUB_INSTAGRAM_ENCRYPTION_KEY");
        _hasRealKey = !string.IsNullOrWhiteSpace(configured);

        var keySource = _hasRealKey ? configured! : DevKey;
        _key = Encoding.UTF8.GetBytes(keySource.PadRight(32)[..32]);

        if (!_hasRealKey)
            _log.LogWarning(
                "InstagramEncryptionService sin clave configurada — usando key de dev (portable entre " +
                "entornos pero débil). Configurá Instagram:EncryptionKey o SALESHUB_INSTAGRAM_ENCRYPTION_KEY " +
                "si querés cifrado fuerte. El login NO depende de que esté presente.");
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        try
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var result = new byte[aes.IV.Length + cipherBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

            return EncPrefix + Convert.ToBase64String(result);
        }
        catch (Exception ex)
        {
            // No queremos perder la cuenta por un fallo de cifrado: guardamos en claro.
            _log.LogError(ex, "Falló el cifrado de la password de IG; se guarda sin cifrar como fallback");
            return plainText;
        }
    }

    public string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;

        // Formato nuevo, prefijado explícitamente.
        if (stored.StartsWith(EncPrefix, StringComparison.Ordinal))
            return TryAesDecrypt(stored[EncPrefix.Length..]) ?? string.Empty;

        // Formato legacy (base64 AES sin prefijo) o texto plano. Intentamos descifrar;
        // si no era ciphertext (o la clave no coincide), lo tratamos como texto plano.
        return TryAesDecrypt(stored) ?? stored;
    }

    private string? TryAesDecrypt(string base64)
    {
        try
        {
            var fullBytes = Convert.FromBase64String(base64);
            using var aes = Aes.Create();
            aes.Key = _key;

            var iv = new byte[aes.IV.Length];
            if (fullBytes.Length <= iv.Length) return null;
            Buffer.BlockCopy(fullBytes, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var cipherBytes = new byte[fullBytes.Length - iv.Length];
            Buffer.BlockCopy(fullBytes, iv.Length, cipherBytes, 0, cipherBytes.Length);

            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // No era base64/ciphertext válido con esta clave → probablemente texto plano.
            return null;
        }
    }
}
