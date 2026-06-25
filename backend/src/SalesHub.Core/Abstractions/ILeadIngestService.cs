using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Abstractions;

public record LeadIngestRequest(
    string ProductKey,
    LeadSource Source,
    string? ExternalId,
    string? Name,
    string? BusinessName,
    string? Phone,
    string? Email);

public enum LeadIngestOutcome
{
    Created,
    Duplicate,
    NoPhone,
    NoProduct
}

public record LeadIngestResult(LeadIngestOutcome Outcome, Guid? LeadId);

/// <summary>
/// Crea un lead entrante (de cualquier fuente) en el embudo: dedup por (producto, teléfono),
/// asigna vendedor, renderiza el mensaje y lo encola en el runner de outreach humanizado.
/// Lo usan tanto el LeadImportWorker (pull del reengage) como el Hub (push de anuncios/OTP/etc).
/// </summary>
public interface ILeadIngestService
{
    Task<LeadIngestResult> IngestAsync(LeadIngestRequest req, CancellationToken ct = default);
}
