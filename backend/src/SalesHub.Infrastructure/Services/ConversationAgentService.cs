using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Procesa los mensajes inbound de WhatsApp: (1) transcribe notas de voz vía
/// Groq/Whisper, (2) genera la respuesta al lead, (3) re-engancha al lead que se
/// quedó callado. Para los productos con AutoPilot=true el bot AUTO-ENVÍA por
/// WhatsApp; si no, deja la respuesta como sugerencia para que el vendedor la mande.
/// </summary>
public class ConversationAgentService
{
    private const int MaxTranscriptionAttempts = 3;
    private const int BatchSize = 10;

    // Re-enganche: cuántas horas de silencio esperamos antes de un nudge, y el tope.
    private const int ReengageAfterHours = 48;
    private const int MaxNudges = 3;

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
        var replied = await GenerateSuggestionsAsync(ct);
        var reengaged = await GenerateReengagementsAsync(ct);
        return transcribed + replied + reengaged;
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
    /// Para los leads cuyo último mensaje es del lead y todavía no tienen respuesta:
    /// genera la respuesta (regla de keyword del vendedor primero, si no Claude) y la
    /// AUTO-ENVÍA si el producto tiene AutoPilot, o la deja como sugerencia si no.
    /// </summary>
    private async Task<int> GenerateSuggestionsAsync(CancellationToken ct)
    {
        // Leads sin sugerencia cuyo último mensaje es del lead. Si el último es
        // "[audio]" todavía sin transcribir, esperamos al próximo tick.
        var candidates = await (
            from l in _db.Leads
            where l.AiSuggestedReply == null && l.SellerId != null
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Inbound
                && last.Text != "[audio]"
            select new { LeadId = l.Id, LastText = last.Text }
        ).Take(BatchSize).ToListAsync(ct);

        var done = 0;
        foreach (var c in candidates)
        {
            var lead = await _db.Leads
                .Include(l => l.Product)
                .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
                .FirstOrDefaultAsync(l => l.Id == c.LeadId, ct);
            if (lead?.Product is null || lead.Seller is null) continue;

            // 1) Reglas de keyword del vendedor — sin IA, sin costo.
            var keywordReply = MatchKeywordRule(lead.Seller.KeywordRules, c.LastText);
            var reply = keywordReply;

            // 2) Fallback a Claude sólo si no matcheó ningún keyword.
            if (reply is null && _suggestions.IsConfigured)
            {
                var thread = await _db.ConversationMessages
                    .Where(m => m.LeadId == lead.Id)
                    .OrderBy(m => m.Timestamp)
                    .ToListAsync(ct);
                reply = await _suggestions.SuggestReplyAsync(lead, lead.Product!, thread, ct);
            }

            if (string.IsNullOrWhiteSpace(reply)) continue;

            var src = keywordReply is not null ? "keyword" : "IA";
            if (lead.Product!.AutoPilot && await AutoSendAsync(lead, reply, ct))
            {
                _log.LogInformation("Auto-respondido a lead {Lead} (fuente: {Src})", lead.Id, src);
            }
            else
            {
                lead.AiSuggestedReply = reply;
                lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                _log.LogInformation("Sugerencia generada para lead {Lead} (fuente: {Src})", lead.Id, src);
            }

            await _db.SaveChangesAsync(ct);
            done++;
        }

        return done;
    }

    /// <summary>
    /// Re-engancha leads que respondieron alguna vez, donde el último mensaje es
    /// nuestro (outbound) y pasaron &gt;= ReengageAfterHours sin novedad. Capea a
    /// MaxNudges por lead. Auto-envía si AutoPilot; si no, deja sugerencia.
    /// </summary>
    private async Task<int> GenerateReengagementsAsync(CancellationToken ct)
    {
        if (!_suggestions.IsConfigured) return 0;

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-ReengageAfterHours);

        var candidates = await (
            from l in _db.Leads
            where l.SellerId != null
                && l.FirstReplyAt != null
                && (l.Status == LeadStatus.Replied || l.Status == LeadStatus.Interested)
                && l.AiSuggestedReply == null
                && l.NudgeCount < MaxNudges
                && (l.LastNudgeAt == null || l.LastNudgeAt < cutoff)
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Outbound
                && last.Timestamp < cutoff
            select new { LeadId = l.Id, LastAt = last.Timestamp }
        ).Take(BatchSize).ToListAsync(ct);

        var done = 0;
        foreach (var c in candidates)
        {
            var lead = await _db.Leads
                .Include(l => l.Product)
                .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
                .FirstOrDefaultAsync(l => l.Id == c.LeadId, ct);
            if (lead?.Product is null || lead.Seller is null) continue;

            var thread = await _db.ConversationMessages
                .Where(m => m.LeadId == lead.Id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync(ct);

            var msg = await _suggestions.SuggestReengagementAsync(
                lead, lead.Product!, thread, now - c.LastAt, ct);
            if (string.IsNullOrWhiteSpace(msg)) continue;

            var autopilot = lead.Product!.AutoPilot;
            if (autopilot)
            {
                if (!await AutoSendAsync(lead, msg, ct)) continue; // no marcar nudge si no se envió
            }
            else
            {
                lead.AiSuggestedReply = msg;
                lead.AiSuggestedReplyAt = now;
            }

            lead.NudgeCount++;
            lead.LastNudgeAt = now;
            lead.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            done++;
            _log.LogInformation("Re-enganche {Mode} a lead {Lead} (nudge {N}/{Max})",
                autopilot ? "auto-enviado" : "sugerido", lead.Id, lead.NudgeCount, MaxNudges);
        }

        return done;
    }

    /// <summary>
    /// Envía un mensaje al lead por la instancia de Evolution de su vendedor y lo
    /// registra como outbound. Devuelve false (sin enviar) si la instancia no está
    /// conectada o falta el teléfono — el caller cae a dejar sugerencia.
    /// </summary>
    private async Task<bool> AutoSendAsync(Lead lead, string text, CancellationToken ct)
    {
        var instance = lead.Seller?.EvolutionInstance;
        if (instance is null || instance.Status != InstanceStatus.Connected) return false;
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return false;

        var ok = await _evo.SendTextAsync(instance.InstanceName, lead.WhatsappPhone, text, ct);
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = ok ? MessageDeliveryStatus.Sent : MessageDeliveryStatus.Failed,
            Text = text,
            EvolutionInstance = instance.InstanceName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        });
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        return ok;
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
