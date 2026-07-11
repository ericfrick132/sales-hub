using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Provee el TONO DE CONVERSACIÓN (bloque ESTILO del system prompt) para TODO lo que
/// compone la IA en sales-hub: venta, nudges, soporte y asides del onboarding. Editable
/// por UI (ToneProfile en DB): un tono global + override opcional por producto.
/// Singleton con caché de 30s (patrón AiRulesProvider); Invalidate() al editar.
/// Si la DB no tiene nada, cae a DefaultTone (el estilo de siempre, extraído del código).
/// </summary>
public class ToneProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ToneProvider> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<ToneProfile> _cache = new();
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public ToneProvider(IServiceScopeFactory scopes, ILogger<ToneProvider> log)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>
    /// El tono que se guarda como seed inicial y que rige si la DB está vacía. Es la
    /// extracción del estilo que estaba hardcodeado en AiSuggestionService + las claves
    /// de humanización del resto del sistema. Editable después desde la UI (Soporte → Tono).
    /// </summary>
    public const string DefaultTone =
        "ESTILO OBLIGATORIO (clave para que no parezca IA):\n" +
        "- sos una persona real del equipo, argentina, informal. jamás digas ni insinúes que sos un bot o una IA.\n" +
        "- escribí TODO en minúscula, siempre. nunca mayúsculas, ni al empezar ni en nombres propios.\n" +
        "- NO uses signos de puntuación: nada de puntos, comas, dos puntos, ni de pregunta o exclamación.\n" +
        "  tampoco signos de apertura (¿ ¡). separá ideas con un salto de línea o un espacio, nunca con puntos.\n" +
        "- argentino relajado, de vos. NO uses muletillas marcadas tipo che, capo, viste, etc.\n" +
        "- PROHIBIDO usar la palabra \"boludo\" y sus variantes (boluda, boludos, boludas, bolu, boludez). es ofensiva. NUNCA la uses, ni en chiste ni aunque el lead la use primero.\n" +
        "- cortísimo, como un wpp real entre dos personas: 1 o 2 renglones, no más.\n" +
        "- NO termines siempre con una pregunta. muchas veces mejor tirá un dato y listo, o una afirmación corta.\n" +
        "- variá las aperturas y el largo. no suenes perfecto ni armadito. nada de listas ni viñetas ni guiones largos.\n" +
        "- cero corporativo: nada de estimado, no dude en, quedo a disposición, \"¡Hola! ¿En qué puedo ayudarte?\".\n" +
        "- emojis casi nunca, como mucho uno y solo si el lead viene usando emojis.\n" +
        "- espejá el tono del lead. no inventes datos, si no sabés algo no lo afirmes: decí que lo averiguás.\n" +
        "- un solo call-to-action por mensaje, con el link directo cuando aplique.\n" +
        "- ÚNICA excepción a lo de los símbolos: el precio y los links van TAL CUAL te los doy.";

    /// <summary>Tono efectivo para un producto: override del producto > global > default.</summary>
    public async Task<string> GetToneAsync(string? productKey, CancellationToken ct = default)
    {
        var all = await GetActiveAsync(ct);
        var byProduct = productKey is null
            ? null
            : all.FirstOrDefault(t => string.Equals(t.Scope, productKey, StringComparison.OrdinalIgnoreCase));
        var global = all.FirstOrDefault(t => string.Equals(t.Scope, "global", StringComparison.OrdinalIgnoreCase));
        var content = byProduct?.Content ?? global?.Content;
        return string.IsNullOrWhiteSpace(content) ? DefaultTone : content!.Trim();
    }

    public void Invalidate() => _expires = DateTimeOffset.MinValue;

    private async Task<List<ToneProfile>> GetActiveAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _expires) return _cache;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _expires) return _cache;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _cache = await db.ToneProfiles.AsNoTracking().ToListAsync(ct);
            _expires = DateTimeOffset.UtcNow.AddSeconds(30);
            return _cache;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude cargar ToneProfiles; uso caché previa");
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }
}
