namespace SalesHub.Core.Domain.Enums;

/// <summary>
/// Canal por el que se envía un <c>MessageOutbox</c>. WhatsApp (vía Evolution)
/// es el default histórico; Instagram lo despacha el worker de DMs de IG.
/// </summary>
public enum MessageChannel
{
    WhatsApp = 0,
    Instagram = 1
}
