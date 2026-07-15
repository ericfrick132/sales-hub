namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Para qué usamos esta cuenta: son DOS cosas distintas. "Inspiration" = su CONTENIDO
/// alimenta los posteos (estilo/ángulo). "FollowSource" = sus SEGUIDORES son el target del
/// auto-follow. A veces la misma cuenta sirve para las dos (Both), a veces no (ej. un estudio
/// de diseño postea zarpado pero sus followers son diseñadores, no clientes).
/// </summary>
public enum CompetitorPurpose
{
    Both = 0,
    Inspiration = 1,
    FollowSource = 2,
}

public class Competitor
{
    public Guid Id { get; set; }
    public string Handle { get; set; } = string.Empty;
    public string Platform { get; set; } = "instagram";
    public string? DisplayName { get; set; }
    public string? Vertical { get; set; }

    /// <summary>Para qué sirve: inspiración de posteos, fuente de follow, o ambas (default).</summary>
    public CompetitorPurpose Purpose { get; set; } = CompetitorPurpose.Both;
    public int? FollowersCount { get; set; }
    public int? FollowingCount { get; set; }
    public int? PostsCount { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RawProfileJson { get; set; }
    public DateTimeOffset? LastScrapedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<CompetitorPost> Posts { get; set; } = new List<CompetitorPost>();
}
