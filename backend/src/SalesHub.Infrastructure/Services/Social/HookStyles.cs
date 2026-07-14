namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Estilos de GANCHO (cómo ABRE el caption). Existe para romper la repetición: sin esto,
/// Claude arranca casi siempre con la misma pregunta retórica ("¿cuántas veces...?"). Cada
/// generación elige uno al azar (con peso), sesgado LEJOS de la pregunta.
/// </summary>
public static class HookStyles
{
    public record HookStyle(string Key, int Weight, string Directive);

    public static readonly IReadOnlyList<HookStyle> All = new List<HookStyle>
    {
        new("escena", 16, "GANCHO = ESCENA: abrí con una escena/momento concreto y visual del día del cliente, en presente (ej: 'Son las 8 y todavía no sabés cuánta plata entró ayer.'). Específico, sin pregunta."),
        new("dato", 14, "GANCHO = DATO: abrí con un número o dato concreto y sorprendente del rubro. Usá SOLO datos reales de la ficha o verdades del rubro, no inventes cifras (ej: 'El 40% de las reservas se piden fuera de horario.')."),
        new("afirmacion", 14, "GANCHO = AFIRMACIÓN: abrí con una afirmación fuerte, directa o contraintuitiva que frene el scroll (NO una pregunta). Ej: 'Tu agenda no está llena. Está desordenada.'"),
        new("historia", 12, "GANCHO = MINI-HISTORIA: abrí contando algo que pasó ('El otro día un dueño de gimnasio nos dijo...'). Anecdótico, humano, en primera persona."),
        new("confesion", 10, "GANCHO = CONFESIÓN: abrí con una admisión honesta o algo que 'no se dice' ('Te lo decimos derecho: perseguir pagos por WhatsApp es una porquería.'). Cercano, sin vender."),
        new("comparacion", 10, "GANCHO = COMPARACIÓN: abrí contrastando dos mundos en una línea ('Antes: tres planillas. Ahora: una pantalla.'). Directo."),
        new("instruccion", 8, "GANCHO = INSTRUCCIÓN: abrí con un imperativo útil ('Dejá de anotar los turnos en papel.'). Consejo directo, sin pregunta."),
        new("frase", 6, "GANCHO = FRASE con punch, tipo cita corta ('Lo que no se mide, se te escapa.'). Después bajás a tierra."),
        new("pregunta", 6, "GANCHO = PREGUNTA (usar POCO): una sola pregunta concreta y fresca. PROHIBIDO el molde '¿cuántas veces...?' / '¿cuánto...?' / '¿sabés...?'. Si no se te ocurre una original, elegí otro arranque."),
    };

    /// <summary>Elige un estilo de gancho por peso (sesgado lejos de la pregunta).</summary>
    public static HookStyle PickRandom(Random rng)
    {
        var total = All.Sum(h => h.Weight);
        var roll = rng.Next(total);
        var acc = 0;
        foreach (var h in All) { acc += h.Weight; if (roll < acc) return h; }
        return All[^1];
    }
}
