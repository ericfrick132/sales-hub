namespace SalesHub.Core.Domain.Entities;

public class SellerDailyStats
{
    public Guid SellerId { get; set; }
    public Seller? Seller { get; set; }
    public DateOnly Date { get; set; }

    public int PlannedCap { get; set; }
    public int MessagesSent { get; set; }
    public int MessagesFailed { get; set; }
    public int Replies { get; set; }
    public int Demos { get; set; }
    public int Closed { get; set; }
    public int Lost { get; set; }
}
