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
    /// lead y todavía no tienen sugerencia. La sugerencia se limpia en cada
    /// inbound nuevo (ConversationService) y cuando el vendedor responde.
    /// </summary>
    private async Task<int> GenerateSuggestionsAsync(CancellationToken ct)
    {
        if (!_suggestions.IsConfigured) return 0;

        // Leads sin sugerencia cuyo último mensaje es del lead. Si el último es
        // "[audio]" todavía sin transcribir, esperamos al próximo tick.
        var needSuggestion = await (
            from l in _db.Leads.Include(x => x.Product)
            where l.AiSuggestedReply == null && l.SellerId != null && l.Product != null
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Inbound
                && last.Text != "[audio]"
            select l
        ).Take(BatchSize).ToListAsync(ct);

        var done = 0;
        foreach (var lead in needSuggestion)
        {
            var thread = await _db.ConversationMessages
                .Where(m => m.LeadId == lead.Id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync(ct);

            var suggestion = await _suggestions.SuggestReplyAsync(lead, lead.Product!, thread, ct);
            if (string.IsNullOrWhiteSpace(suggestion)) continue;

            lead.AiSuggestedReply = suggestion;
            lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            done++;
            _log.LogInformation("Sugerencia IA generada para lead {Lead}", lead.Id);
        }

        return done;
    }
}
