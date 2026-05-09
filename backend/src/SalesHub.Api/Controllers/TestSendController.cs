using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Probar la cadencia configurada del producto disparándola contra un número
/// arbitrario, sin pasar por el flujo de leads ni la cola humanizada. Útil
/// para validar audios/textos/adjuntos antes de habilitar el envío real.
///
/// Manda los steps en orden RESPETANDO el delaySeconds configurado del paso,
/// con un cap de 10 minutos para no colgar la request. Si tu delay real es
/// mayor, usá el flujo real (asignar lead + queue) en vez del panel de prueba.
/// Si el step tiene varios audios, mandamos el primero (no rotamos: la prueba
/// quiere ser determinística).
/// </summary>
[ApiController]
[Route("api/test-send")]
[Authorize]
public class TestSendController : ControllerBase
{
    private const int MaxStepDelaySeconds = 600;
    // Delay corto fijo entre el texto previo y el audio dentro de un mismo
    // step (legacy; los steps nuevos no permiten texto + audio en el mismo).
    private const int IntraStepDelayMs = 1500;
    // Cliente de WhatsApp suele dejar de mostrar "recording…" después de
    // ~25-30s aunque sigamos diciéndoselo a Evolution. Refrescamos en chunks.
    private const int PresenceChunkSeconds = 25;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly IMessageRenderer _renderer;
    private readonly ILogger<TestSendController> _log;

    public TestSendController(ApplicationDbContext db, IEvolutionClient evo, IMessageRenderer renderer, ILogger<TestSendController> log)
    {
        _db = db; _evo = evo; _renderer = renderer; _log = log;
    }

    public record CadenceRequest(Guid ProductId, Guid SellerId, string Phone, string? Category);

    [HttpPost("cadence")]
    public async Task<IActionResult> SendCadence([FromBody] CadenceRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (req.ProductId == Guid.Empty) return BadRequest(new { error = "productId requerido" });
        if (req.SellerId == Guid.Empty) return BadRequest(new { error = "sellerId requerido" });

        var phone = new string((req.Phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(phone)) return BadRequest(new { error = "Número inválido (incluí prefijo de país)" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == req.ProductId, ct);
        if (product is null) return NotFound(new { error = "Producto no existe" });

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance)
            .FirstOrDefaultAsync(s => s.Id == req.SellerId, ct);
        if (seller?.EvolutionInstance is null) return BadRequest(new { error = "Vendedor sin instancia Evolution" });

        // Re-verificar live si la DB dice Disconnected — InstanceMonitor
        // puede estar stale entre ticks.
        if (seller.EvolutionInstance.Status != InstanceStatus.Connected)
        {
            var live = await _evo.GetInstanceStatusAsync(seller.EvolutionInstance.InstanceName, ct);
            if (live.Status is not "open" and not "connected")
                return BadRequest(new { error = $"La instancia no está conectada (status real: {live.Status})" });
            seller.EvolutionInstance.Status = InstanceStatus.Connected;
            await _db.SaveChangesAsync(ct);
        }

        var instance = seller.EvolutionInstance.InstanceName;

        // Lead "fake" para que el renderer pueda llenar placeholders. Usamos
        // valores neutros que sirven para preview. SearchCategory dispara la
        // selección de cadencia override (si el seller eligió categoría).
        var fakeLead = new Lead
        {
            Name = "(prueba)",
            City = "",
            Province = "",
            ProductKey = product.ProductKey,
            WhatsappPhone = phone,
            SearchCategory = req.Category
        };

        var (steps, _) = OutboxEnqueueHelper.ResolveStepsForLead(fakeLead, product);
        if (steps.Count == 0) return BadRequest(new
        {
            error = string.IsNullOrWhiteSpace(req.Category)
                ? "El producto no tiene cadencia configurada"
                : $"El producto no tiene cadencia configurada para la categoría '{req.Category}' ni default"
        });

        var jid = $"{phone}@s.whatsapp.net";
        var sentSteps = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var hasMedia = step.MediaAssetId is not null || (step.MediaAssetIds is { Count: > 0 });
            if (string.IsNullOrWhiteSpace(step.Text) && !hasMedia) continue;

            Guid? mediaAssetId = step.MediaAssetIds is { Count: > 0 } ? step.MediaAssetIds[0] : step.MediaAssetId;

            // Delay del paso: silencio. El indicador "grabando audio…" se
            // muestra recién cuando vamos a enviar el audio, y dura exacto
            // lo que dura el audio.
            if (i > 0)
            {
                var d = Math.Min(Math.Max(0, step.DelaySeconds), MaxStepDelaySeconds);
                if (d > 0) await Task.Delay(d * 1000, ct);
            }

            var rendered = string.IsNullOrWhiteSpace(step.Text)
                ? string.Empty
                : _renderer.RenderTemplate(step.Text, fakeLead, product, seller);

            try
            {
                if (mediaAssetId is null)
                {
                    if (!string.IsNullOrWhiteSpace(rendered))
                    {
                        var ok = await _evo.SendTextAsync(instance, phone, rendered, ct);
                        if (!ok) return StatusCode(502, new { error = $"Falló el step {i + 1} (texto)" });
                    }
                }
                else
                {
                    var asset = await _db.MediaAssets.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.Id == mediaAssetId, ct);
                    if (asset is null) return BadRequest(new { error = $"Step {i + 1}: media no existe" });

                    if (asset.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(rendered))
                        {
                            var pre = await _evo.SendTextAsync(instance, phone, rendered, ct);
                            if (!pre) return StatusCode(502, new { error = $"Falló el step {i + 1} (texto previo al audio)" });
                            await Task.Delay(IntraStepDelayMs, ct);
                        }
                        var prep = await _evo.PrepareVoiceNoteAsync(asset.Content, ct);
                        var dur = Math.Max(1, prep.DurationSeconds);
                        var rem = dur;
                        while (rem > 0)
                        {
                            var chunk = Math.Min(PresenceChunkSeconds, rem);
                            await _evo.SetPresenceRecordingAsync(instance, jid, chunk, ct);
                            await Task.Delay(chunk * 1000, ct);
                            rem -= chunk;
                        }
                        var okv = await _evo.SendPreparedVoiceNoteAsync(instance, phone, prep.OggBytes, ct);
                        if (!okv) return StatusCode(502, new { error = $"Falló el step {i + 1} (audio)" });
                    }
                    else
                    {
                        var caption = string.IsNullOrWhiteSpace(rendered) ? null : rendered;
                        var okm = await _evo.SendMediaAsync(instance, phone, asset.Content, asset.MimeType, asset.FileName, caption, ct);
                        if (!okm) return StatusCode(502, new { error = $"Falló el step {i + 1} (adjunto)" });
                    }
                }
                sentSteps++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TestSend cadence step {Step} failed", i + 1);
                return StatusCode(502, new { error = $"Step {i + 1}: {ex.Message}" });
            }
        }

        return Ok(new { ok = true, sentSteps, totalSteps = steps.Count });
    }
}
