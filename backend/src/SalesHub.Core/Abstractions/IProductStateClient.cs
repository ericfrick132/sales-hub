namespace SalesHub.Core.Abstractions;

/// <summary>
/// Estado de un lead B2B según su producto de origen. El producto decide <see cref="StopFollowup"/>
/// (ya no hay que perseguirlo: completó setup / pagó / churneó) y <see cref="Won"/> (true = se
/// convirtió/reactivó; false = se perdió). SalesHub solo actúa, no replica la lógica de cada app.
/// </summary>
public record LeadStateResult(string ExternalId, bool StopFollowup, bool Won, string? Status);

public interface IProductStateClient
{
    Task<IReadOnlyList<LeadStateResult>> GetStatesAsync(
        string stateUrl, string apiKey, IReadOnlyCollection<string> externalIds, CancellationToken ct = default);
}
