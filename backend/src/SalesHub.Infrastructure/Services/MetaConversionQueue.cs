using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cola en memoria de los avisos a Meta (<see cref="MetaConversionService"/>). Quien llama (el bot, el CRM)
/// encola DESPUÉS de guardar y sigue: no espera a Meta, no ve errores y no puede romper la charla. Los avisos
/// salen de a uno, cada uno con su propio scope de DI (su DbContext). Singleton y sin hosted service, así anda
/// igual en la API (que también corre los workers) y en cualquier otro host que registre la infraestructura.
/// Si el proceso se reinicia con algo en la cola, eso se pierde (segundos); <c>POST /api/meta-capi/retry</c>
/// recupera lo que haya quedado colgado en la tabla.
/// </summary>
public sealed class MetaConversionQueue
{
    public record Item(Guid LeadId, string Stage, string Trigger, DateTimeOffset OccurredAt);

    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MetaConversionQueue> _log;
    private int _running;

    public MetaConversionQueue(IServiceScopeFactory scopes, ILogger<MetaConversionQueue> log)
    {
        _scopes = scopes; _log = log;
    }

    /// <summary>Encola las etapas que alcanzó el lead. Nunca tira.</summary>
    public void Enqueue(Guid leadId, IEnumerable<string> stages, string trigger)
    {
        try
        {
            var any = false;
            foreach (var stage in stages.Where(MetaFunnel.IsStage).Distinct())
                any |= _channel.Writer.TryWrite(new Item(leadId, stage, trigger, DateTimeOffset.UtcNow));
            if (any && Interlocked.CompareExchange(ref _running, 1, 0) == 0) _ = Task.Run(DrainAsync);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Meta CAPI: no pude encolar el aviso del lead {Lead}", leadId);
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            while (_channel.Reader.TryRead(out var item))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<MetaConversionService>();
                    await svc.TrackAsync(item.LeadId, item.Stage, item.Trigger, item.OccurredAt, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Meta CAPI: falló el aviso {Stage} del lead {Lead}", item.Stage, item.LeadId);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            // Lo que entró mientras se cerraba el drenaje no se queda esperando al próximo aviso.
            if (_channel.Reader.Count > 0 && Interlocked.CompareExchange(ref _running, 1, 0) == 0) _ = Task.Run(DrainAsync);
        }
    }
}
