namespace SalesHub.Core.Domain.Enums;

/// <summary>Ventana sobre la que se acumula un objetivo de vendedor.</summary>
public enum SellerGoalPeriod
{
    /// <summary>Día actual (zona horaria AR).</summary>
    Daily = 0,

    /// <summary>Semana actual (lunes 00:00 AR a domingo).</summary>
    Weekly = 1
}
