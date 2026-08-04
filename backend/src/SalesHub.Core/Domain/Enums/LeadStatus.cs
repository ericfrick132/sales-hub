namespace SalesHub.Core.Domain.Enums;

public enum LeadStatus
{
    New = 0,
    Assigned = 1,
    Queued = 2,
    Sent = 3,
    Replied = 4,
    Interested = 5,
    DemoScheduled = 6,
    Closed = 7,
    Lost = 8,
    Blocked = 9,
    /// <summary>
    /// El número no tiene cuenta de WhatsApp. Lo detecta el bridge: al abrir el chat
    /// aparece la pantalla de "invitar a WhatsApp" en vez de la conversación. Terminal:
    /// no se le encola más cadencia (ver OutboxEnqueueHelper).
    /// </summary>
    NoWhatsApp = 10
}
