using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

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

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly IMessageRenderer _renderer;
    private readonly ILogger<TestSendController> _log;

    public TestSendController(ApplicationDbContext db, IEvolutionClient evo, IMessageRenderer renderer, ILogger<TestSendController> log)
    {
        _db = db; _evo = evo; _renderer = renderer; _log = log;
    }

    public record CadenceRequest(Guid ProductId, Guid SellerId, string Phone);

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
        if (seller.EvolutionInstance.Status != InstanceStatus.Connected) return BadRequest(new { error = "La instancia no está conectada" });

        var steps = product.MessageSteps ?? new();
        if (steps.Count == 0) return BadRequest(new { error = "El producto no tiene cadencia configurada" });

        var instance = seller.EvolutionInstance.InstanceName;

        // Lead "fake" para que el renderer pueda llenar placeholders. Usamos
        // valores neutros que sirven para preview.
        var fakeLead = new Lead
        {
            Name = "(prueba)",
            City = "",
            Province = "",
            ProductKey = product.ProductKey,
            WhatsappPhone = phone
        };

        var sentSteps = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var hasMedia = step.MediaAssetId is not null || (step.MediaAssetIds is { Count: > 0 });
            if (string.IsNullOrWhiteSpace(step.Text) && !hasMedia) continue;

            // Esperar el delay configurado del paso antes de enviarlo. El step 0
            // siempre arranca inmediato. Capeo a 10 min para no colgar la request.
            if (i > 0)
            {
                var d = Math.Min(Math.Max(0, step.DelaySeconds), MaxStepDelaySeconds);
                if (d > 0) await Task.Delay(d * 1000, ct);
            }

            var rendered = string.IsNullOrWhiteSpace(step.Text)
                ? string.Empty
                : _renderer.RenderTemplate(step.Text, fakeLead, product, seller);

            // Resolver el asset del step:
            // - Si tiene variantes, mandamos la primera (test determinístico, no rota).
            // - Si tiene mediaAssetId legacy, ese.
            // - Si no, solo texto.
            Guid? mediaAssetId = step.MediaAssetIds is { Count: > 0 } ? step.MediaAssetIds[0] : step.MediaAssetId;

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
                        var okv = await _evo.SendVoiceNoteAsync(instance, phone, asset.Content, ct);
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
