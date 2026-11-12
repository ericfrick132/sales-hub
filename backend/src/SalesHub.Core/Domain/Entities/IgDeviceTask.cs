namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Una indicación que SalesHub le dio a un celular para Instagram: confirmar la cuenta, leer los
/// seguidores o mandar un DM. Queda como bitácora (qué se pidió, cómo terminó y qué había en
/// pantalla si falló) y es lo que evita darle dos cosas a la vez al mismo celular.
/// </summary>
public class IgDeviceTask
{
    public const string KindVerify = "verify";
    public const string KindFollowers = "followers";
    public const string KindCold = "dm_cold";
    public const string KindFollower = "dm_follower";
    /// <summary>DM de prueba pedido a mano: saltea topes y horario, no toca ningún lead.</summary>
    public const string KindTest = "test";

    public const string StatusQueued = "queued";
    public const string StatusRunning = "running";
    public const string StatusOk = "ok";
    public const string StatusFailed = "failed";

    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public Guid DeviceId { get; set; }

    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = StatusQueued;

    public Guid? LeadId { get; set; }
    public Guid? FollowerId { get; set; }

    /// <summary>@usuario destino (o la cuenta propia en verify/followers).</summary>
    public string? Handle { get; set; }

    /// <summary>Texto que se le pidió tipear, tal cual sale.</summary>
    public string? Text { get; set; }

    public string? Error { get; set; }

    /// <summary>Resumen de lo que había en pantalla cuando falló: sirve para corregir los pasos sin tener el celular en la mano.</summary>
    public string? Screen { get; set; }

    /// <summary>Cuántos seguidores trajo (solo en followers).</summary>
    public int? Collected { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
