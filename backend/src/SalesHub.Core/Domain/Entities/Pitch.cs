namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// PITCH por anuncio (modelo "Smart Setter"/GHL): guion de pasos que se dispara cuando un
/// lead entra por un anuncio click-to-WhatsApp. Cada paso es un GRUPO de mensajes que salen
/// juntos (texto / media / audio, con delay en segundos entre ellos). La RESPUESTA del lead
/// avanza al paso siguiente; si no responde, salen los follow-ups del paso (en horas). Cuando
/// se completan todos los pasos, la IA contesta libre (<see cref="AiAfterPitch"/>) o la charla
/// pasa a un humano (bot muteado).
///
/// Matching (en este orden): <see cref="AdIds"/> contra el `sourceId` del externalAdReply
/// que trae WhatsApp en cada CTWA → <see cref="TriggerText"/> contenido en el primer mensaje
/// (el texto prellenado del anuncio) → <see cref="IsDefault"/> del producto.
/// </summary>
public class Pitch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductKey { get; set; } = string.Empty;
    public Product? Product { get; set; }
    /// <summary>Nombre visible ("AD 1 - placa tp-a: ¿cuántos turnos perdiste?").</summary>
    public string Name { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>Ids de anuncio de Meta (externalAdReply.sourceId) que enrolan en este pitch.</summary>
    public List<string> AdIds { get; set; } = new();
    /// <summary>Fragmento del texto prellenado del anuncio (case-insensitive) que enrola en este pitch.</summary>
    public string? TriggerText { get; set; }
    /// <summary>Pitch por defecto del producto: cualquier lead de anuncio sin match específico cae acá.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Pasos del guion (jsonb).</summary>
    public List<PitchStep> Steps { get; set; } = new();

    /// <summary>Tag que se le pega al lead cuando responde después de recibir el pitch (ej. "respondio").</summary>
    public string? AutoTagOnReply { get; set; }
    /// <summary>Etapa/estado del CRM al que se mueve el lead cuando responde (null = no tocar).</summary>
    public string? StatusOnReply { get; set; } = "Interested";
    /// <summary>
    /// true = terminado el guion, la IA sigue la charla sola. false = handoff humano: el bot
    /// queda muteado y el chat aparece para que lo tome el vendedor.
    /// </summary>
    public bool AiAfterPitch { get; set; } = true;
    /// <summary>Segundos mínimos/máximos de espera "humana" antes de mandar cada paso (desde el mensaje del lead).</summary>
    public int ReplyDelayMinSec { get; set; } = 8;
    public int ReplyDelayMaxSec { get; set; } = 40;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Un paso: N mensajes que salen juntos + follow-ups si el lead no contesta.</summary>
public class PitchStep
{
    public string? Title { get; set; }
    public List<PitchMessage> Messages { get; set; } = new();
    public List<PitchFollowUp> FollowUps { get; set; } = new();
}

public class PitchMessage
{
    /// <summary>Texto (placeholders {name} {seller} {price} etc. como MessageTemplate). Puede ser vacío si es solo media.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Adjunto (imagen/video/pdf) o AUDIO — si el mime es audio/* sale como nota de voz.</summary>
    public Guid? MediaAssetId { get; set; }
    /// <summary>
    /// Nota de voz GENERADA con la voz clonada (ElevenLabs) renderizando este texto con los
    /// placeholders del lead. Si está, gana sobre MediaAssetId.
    /// </summary>
    public string? VoiceText { get; set; }
    /// <summary>Segundos de espera ANTES del siguiente mensaje del mismo paso.</summary>
    public int DelaySeconds { get; set; } = 5;
}

public class PitchFollowUp
{
    /// <summary>Horas después de enviado el paso (o del follow-up anterior) sin respuesta.</summary>
    public double AfterHours { get; set; } = 1;
    public string Text { get; set; } = string.Empty;
    public Guid? MediaAssetId { get; set; }
}
