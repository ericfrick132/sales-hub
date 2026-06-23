using System.ComponentModel.DataAnnotations;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Flag on/off persistido en DB para prender/apagar runners desde la UI sin tocar env vars.
/// Si no existe la fila, el worker cae al valor de config (Workers:*AutoStart, Seo:AutoStart).
/// Keys: "posteos", "captacion", "seo".
/// </summary>
public class RuntimeFlag
{
    [Key]
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
