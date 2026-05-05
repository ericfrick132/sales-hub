namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Un paso del drip de outreach inicial. Soporta los mismos placeholders y
/// spin-text que MessageTemplate. DelaySeconds es relativo al paso anterior
/// (el primer step se manda al asignar; los siguientes esperan ese delta
/// además de la humanización del seller).
/// </summary>
public class MessageStep
{
    public string Text { get; set; } = string.Empty;
    public int DelaySeconds { get; set; }
    /// <summary>Si está, el step manda el archivo (imagen/PDF) y el Text se usa como caption.</summary>
    public Guid? MediaAssetId { get; set; }
}
