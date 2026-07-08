namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Notas de voz IA en conversaciones: en momentos decisivos de la venta el bot responde
/// con un audio PTT con la voz clonada de Eric en vez de texto. El guion lo reescribe
/// Claude a partir de la respuesta generada, siguiendo la receta de pronunciación
/// aprobada (voseo con tilde, "shama", montos en lucas, conectores reales, cierre que cae).
/// Gateado por el runtime flag 'voicenote'.
/// </summary>
public class VoiceNoteOptions
{
    /// <summary>Voz clonada "Eric Frick v3" (entrenada con sus notas de voz reales).</summary>
    public string VoiceId { get; set; } = "Vbl3y1UnPB4uDeRu4IEc";

    // Settings ganadores del A/B con el user (2026-07-07).
    public double Stability { get; set; } = 0.45;
    public double SimilarityBoost { get; set; } = 0.85;
    public double Style { get; set; } = 0.15;
    public double Speed { get; set; } = 0.93;

    /// <summary>Probabilidad de responder en audio cuando el lead mandó nota de voz (espejo).</summary>
    public double MirrorProbability { get; set; } = 0.8;

    /// <summary>Probabilidad de audio en un momento decisivo (precio/cuenta/demo/interesado).</summary>
    public double DecisiveProbability { get; set; } = 0.35;

    /// <summary>Probabilidad de audio en la despedida (cierra con "un abrazo").</summary>
    public double FarewellProbability { get; set; } = 0.6;

    /// <summary>No repetir audio al mismo lead dentro de esta ventana.</summary>
    public int CooldownHours { get; set; } = 24;

    /// <summary>Tope global de audios por día (guarda de créditos ElevenLabs).</summary>
    public int MaxPerDay { get; set; } = 20;

    /// <summary>Largo máximo del guion en palabras (~25 segundos de audio).</summary>
    public int MaxScriptWords { get; set; } = 70;
}
