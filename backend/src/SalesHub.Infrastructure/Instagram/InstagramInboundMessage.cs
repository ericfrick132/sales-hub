namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Un mensaje entrante (DM) leído del inbox de Instagram.
/// </summary>
/// <param name="Handle">Username del remitente (sin @).</param>
/// <param name="ExternalId">item_id de Instagram, estable — se usa para dedup.</param>
/// <param name="Text">Texto del mensaje.</param>
/// <param name="Timestamp">Momento del mensaje (derivado del timestamp de IG).</param>
public record InstagramInboundMessage(string Handle, string ExternalId, string Text, DateTimeOffset Timestamp);
