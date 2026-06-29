using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Distribuidor Warmr por HANDOFF (soportado): no sube nada automático — deja el
/// posteo en la cola <c>ReadyForWarmr</c> con su asset + caption + cuenta + slot,
/// para que el humano haga el último drag a Cloud Drop. Robusto y nunca se rompe.
/// </summary>
public class WarmrHandoffDistributor : IWarmrDistributor
{
    private readonly WarmrOptions _opts;

    public WarmrHandoffDistributor(IOptions<WarmrOptions> opts) { _opts = opts.Value; }

    public bool IsConfigured => true; // el handoff no necesita config

    public string Mode => WarmrDispatchMode.Handoff;

    public Task<WarmrDispatchResult> DispatchAsync(SocialPost post, CancellationToken ct = default)
    {
        post.Target = SocialDistribution.Warmr;
        post.Status = SocialPostStatus.ReadyForWarmr;
        post.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(new WarmrDispatchResult(true));
    }
}
