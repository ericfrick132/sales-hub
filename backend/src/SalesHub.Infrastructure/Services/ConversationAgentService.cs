using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Procesa los mensajes inbound de WhatsApp: (1) transcribe las notas de voz a
/// texto vía Groq/Whisper, (2) genera la respuesta sugerida por IA (modo
/// asistido — el vendedor revisa y manda). Cada paso es independiente y se
/// saltea si su API key no está configurada.
/// </summary>
public class ConversationAgentService
{
    private const int MaxTranscriptionAttempts = 3;
    private const int BatchSize = 10;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly GroqWhisperClient _whisper;
    private readonly AiSuggestionService _suggestions;
    private readonly ILogger<ConversationAgentService> _log;

    public ConversationAgentService(
        ApplicationDbContext db, IEvolutionClient evo, GroqWhisperClient whisper,
        AiSuggestionService suggestions, ILogger<ConversationAgentService> log)
    {
        _db = db; _evo = evo; _whisper = whisper; _suggestions = suggestions; _log = log;
    }

    public async Task<int> TickAsync(CancellationToken ct)
    {
        var transcribed = await TranscribeAudiosAsync(ct);
        var suggested = await GenerateSuggestionsAsync(ct);
        return transcribed + suggested;
    }

    /// <summary>Transcribe las notas de voz inbound que el webhook dejó como "[audio]".</summary>
    private async Task<int> TranscribeAudiosAsync(CancellationToken ct)
    {
        // Sin API key de Groq no tocamos nada — los audios quedan como "[audio]".
        if (!_whisper.IsConfigured) return 0;

        var pending = await _db.ConversationMessages
            .Where(m => m.Direction == MessageDirection.Inbound
                     && m.Text == "[audio]"
                     && m.TranscriptionAttempts < MaxTranscriptionAttempts
                     && m.RawJson != null
                     && m.EvolutionInstance != null)
            .OrderBy(m => m.Timestamp)
            .Take(BatchSize)
            .ToListAsync(ct);

        var done = 0;
        foreach (var msg in pending)
        {
            // Incrementamos el contador ANTES de intentar — si crashea a mitad no
            // quedamos reintentando infinito el mismo audio roto.
            msg.TranscriptionAttempts++;

            string? transcript = null;
            var audio = await _evo.GetMediaBase64Async(msg.EvolutionInstance!, msg.RawJson!, ct);
            if (audio is not null)
                transcript = await _whisper.TranscribeAsync(audio, "voice.ogg", ct);

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                msg.Text = $"🎤 {transcript}";
                done++;
                _log.LogInformation("Audio transcripto: msg={Id} lead={Lead}", msg.Id, msg.LeadId);
            }
            else if (msg.TranscriptionAttempts >= MaxTranscriptionAttempts)
            {
                // Agotó los intentos: lo dejamos legible en vez de "[audio]".
                msg.Text = "[audio — no se pudo transcribir]";
                _log.LogWarning("Audio msg {Id} agotó los {N} intentos de transcripción",
                    msg.Id, MaxTranscriptionAttempts);
            }
            // else: queda "[audio]" con el contador subido, reintenta en el próximo tick.

            await _db.SaveChangesAsync(ct);
        }

        return done;
    }

    /// <summary>
    /// Genera la respuesta sugerida para los leads cuyo último mensaje es del
    /// lead y todavía no tienen sugerencia. Primero prueba las reglas de keyword
    /// del vendedor (sin IA); si no matchea ninguna, cae a Claude. La sugerencia
    /// se limpia en cada inbound nuevo (ConversationService) y al responder.
    /// </summary>
    private async Task<int> GenerateSuggestionsAsync(CancellationToken ct)
    {
        // Leads sin sugerencia cuyo último mensaje es del lead. Si el último es
        // "[audio]" todavía sin transcribir, esperamos al próximo tick.
        // Proyectamos el texto del último mensaje para chequear keywords sin
        // tener que cargar el hilo entero.
        var candidates = await (
            from l in _db.Leads.Include(x => x.Product).Include(x => x.Seller)
            where l.AiSuggestedReply == null && l.SellerId != null
                  && l.Product != null && l.Seller != null
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Inbound
                && last.Text != "[audio]"
            select new { Lead = l, LastText = last.Text }
        ).Take(BatchSize).ToListAsync(ct);

        var done = 0;
        foreach (var c in candidates)
        {
            var lead = c.Lead;

            // 1) Reglas de keyword del vendedor — sin IA, sin costo.
            var keywordReply = MatchKeywordRule(lead.Seller!.KeywordRules, c.LastText);
            var suggestion = keywordReply;

            // 2) Fallback a Claude sólo si no matcheó ningún keyword.
            if (suggestion is null && _suggestions.IsConfigured)
            {
                var thread = await _db.ConversationMessages
                    .Where(m => m.LeadId == lead.Id)
                    .OrderBy(m => m.Timestamp)
                    .ToListAsync(ct);
                suggestion = await _suggestions.SuggestReplyAsync(lead, lead.Product!, thread, ct);
            }

            if (string.IsNullOrWhiteSpace(suggestion)) continue;

            lead.AiSuggestedReply = suggestion;
            lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            done++;
            _log.LogInformation("Sugerencia generada para lead {Lead} (fuente: {Src})",
                lead.Id, keywordReply is not null ? "keyword" : "IA");
        }

        return done;
    }

    /// <summary>
    /// Devuelve la respuesta de la primera regla "keyword = respuesta" cuyo
    /// keyword aparezca (case-insensitive) en el texto del lead. null si no
    /// matchea ninguna. Soporta "\n" literal en la respuesta como salto de línea.
    /// </summary>
    private static string? MatchKeywordRule(IEnumerable<string> rules, string leadText)
    {
        if (string.IsNullOrWhiteSpace(leadText)) return null;
        foreach (var rule in rules)
        {
            var eq = rule.IndexOf('=');
            if (eq <= 0) continue;
            var keyword = rule[..eq].Trim();
            var reply = rule[(eq + 1)..].Trim().Replace("\\n", "\n");
            if (keyword.Length == 0 || reply.Length == 0) continue;
            if (leadText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return reply;
        }
        return null;
    }
}
