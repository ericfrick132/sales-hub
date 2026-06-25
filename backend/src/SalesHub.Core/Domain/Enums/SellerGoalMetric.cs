namespace SalesHub.Core.Domain.Enums;

/// <summary>
/// Métrica sobre la que se mide un objetivo de vendedor. El "actual" de cada una
/// se calcula contando leads del vendedor (y opcionalmente de una app) en el
/// período, usando los timestamps de la entidad Lead.
/// </summary>
public enum SellerGoalMetric
{
    /// <summary>Leads con primer contacto saliente (Lead.SentAt) en el período.</summary>
    Contactos = 0,

    /// <summary>Leads que respondieron (Lead.FirstReplyAt) en el período.</summary>
    Respuestas = 1,

    /// <summary>Leads que pasaron a demo agendada (Lead.DemoScheduledAt) en el período.</summary>
    Demos = 2,

    /// <summary>Leads cerrados (Status=Closed, Lead.ClosedAt) en el período.</summary>
    Cierres = 3
}
