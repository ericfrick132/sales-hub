namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Guía de estilo de la voz de Eric: muletillas/conectores NO informativos minados de sus
/// audios reales de venta (corpus: 23 notas de voz naturales, frecuencias por mil palabras).
/// Es lo que hace que un guion suene argentino y PROPIO de Eric en vez de robótico.
/// La usan el generador de guiones del bot (VoiceNoteService) y la adaptación literal de
/// /voice-test. Actualizarla cuando la calibración descubra muletillas nuevas.
/// </summary>
public static class EricVoiceStyle
{
    /// <summary>
    /// Repertorio con posición y dosis. Fuente: transcripciones Whisper de sus audios reales
    /// (2026-07). "ahí" 12.4‰, "nada," 10.6‰, "así que (nada)" 7‰, "bueno," 6.5‰, resto 1-4‰.
    /// </summary>
    public const string Guide =
        "GUÍA DE ESTILO ERIC (muletillas reales minadas de sus audios — esto hace que suene a él):\n" +
        "• ARRANQUES: \"Bueno,\" / \"Mirá,\" / \"Escuchame,\" / \"Che,\" / \"¿Qué hacés? ¿Todo bien?\" / " +
        "\"Sí, por supuesto, te entiendo\" / \"Dale, buenísimo.\"\n" +
        "• HILADO (en el medio): \"ahí\" es SU marca — usa \"ahí te paso\", \"ahí podés cargar\", \"ahí configurás\" " +
        "en vez de \"te paso\", \"podés cargar\". También: \"nada,\" / \"bueno, nada,\" / \"obviamente\" / " +
        "\"justamente\" / \"viste\" / \"o sea\" / \"igual...\" / \"la verdad\" / \"te digo\" / \"más o menos\" / " +
        "\"aparte\" / \"después\" / \"un montón\" / \"súper\" / \"tranqui\" / \"etcétera\" / \"y todo\" / \"pero bueno\".\n" +
        "• CIERRES: \"así que nada,\" + afirmación corta / \"cualquier cosa (cosita) me escribís por acá\" / " +
        "\"no hay problema\" / \"no hay drama\" / coletilla \", ¿dale?\" / en despedidas: \"un abrazo\" al final.\n" +
        "• DOSIS: 1 arranque + 1 o 2 de hilado + 1 cierre por audio. NO amontonar muletillas: una cada " +
        "15-20 palabras suena a él; una por frase suena a parodia.";
}
