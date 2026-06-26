namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Una variante de audio del pitch del onboarding, grabada desde la plataforma. Se guardan varias
/// por app y el motor rota al azar (para que Meta no fichee siempre el mismo archivo). Se envía
/// como nota de voz por Evolution justo después de las preguntas.
/// </summary>
public class OnboardingAudio
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductKey { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string MimeType { get; set; } = "audio/ogg";
    public int DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
