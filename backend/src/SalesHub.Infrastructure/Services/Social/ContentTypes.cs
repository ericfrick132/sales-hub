namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Catálogo de TIPOS de posteo. La idea es que el feed no sea siempre "venta":
/// la mayoría del contenido es orgánico (emoción, valor, comunidad) y solo a
/// veces vende o muestra precio. Cada tipo trae una directiva que cambia el
/// ÁNGULO del posteo (no la marca). El generador elige uno con peso, evitando
/// repetir el último.
/// </summary>
public static class ContentTypes
{
    public record ContentType(string Key, string Label, int Weight, string Directive);

    /// <summary>
    /// Weight = probabilidad relativa. Sesgado a lo orgánico: venta y precio son
    /// minoría. El precio SOLO se usa si hay datos reales de la landing.
    /// </summary>
    public static readonly IReadOnlyList<ContentType> All = new List<ContentType>
    {
        new("emocional", "Emocional / historia", 30,
            "TIPO: EMOCIONAL. Contá una historia o un momento del mundo del cliente que genere una emoción (calma, orgullo, pertenencia, alivio, motivación). NADA de venta, features ni precio. La marca aparece como telón de fondo, no como protagonista. El visual tiene que TRANSMITIR ese sentimiento, no mostrar el producto."),
        new("educativo", "Educativo / valor", 22,
            "TIPO: EDUCATIVO. Aportá un tip, dato o mini-consejo útil del rubro del cliente, que sirva aunque nunca compre el producto. Generoso, sin CTA de venta. El producto puede insinuarse al final, suave."),
        new("comunidad", "Comunidad / relatable", 18,
            "TIPO: COMUNIDAD. Algo relatable o con humor sobre el día a día del cliente (lo memeable, lo que solo entiende quien vive ese rubro). Liviano, para que comente y comparta. Cero venta."),
        new("detras", "Detrás de escena", 10,
            "TIPO: DETRÁS DE ESCENA. Mostrá la cultura/proceso/gente detrás de la marca, o cómo se construye el producto. Humaniza, genera confianza. No es un anuncio."),
        new("venta", "Venta / beneficio", 15,
            "TIPO: VENTA. Comunicá UN beneficio concreto del producto con un CTA suave. Usá SOLO hechos reales de la ficha de la landing (no inventes features). Sigue siendo atractivo, no un folleto."),
        new("precio", "Precio / promo", 5,
            "TIPO: PRECIO/PROMO. Comunicá el precio o una oferta puntual. Usá EXCLUSIVAMENTE los precios/planes que estén en la ficha de la landing; si la ficha no tiene precios, NO inventes — elegí otro ángulo. CTA claro."),
    };

    public static ContentType? ByKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(t => t.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Elige un tipo por peso, evitando <paramref name="avoidKey"/> (el último usado).
    /// <paramref name="hasLandingPrices"/> saca "precio" de la baraja si no hay datos reales.
    /// </summary>
    public static ContentType Pick(string? avoidKey, bool hasLandingPrices, Random rng)
    {
        var pool = All.Where(t => !string.Equals(t.Key, avoidKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!hasLandingPrices) pool = pool.Where(t => t.Key != "precio").ToList();
        if (pool.Count == 0) pool = All.ToList();

        var total = pool.Sum(t => t.Weight);
        var roll = rng.Next(total);
        var acc = 0;
        foreach (var t in pool) { acc += t.Weight; if (roll < acc) return t; }
        return pool[^1];
    }
}
