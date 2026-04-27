namespace SalesHub.Api.Dtos;

public record SellerMetricRow(
    Guid SellerId,
    string DisplayName,
    int LeadsAssigned,
    int LeadsSent,
    int LeadsReplied,
    int LeadsClosed,
    double ReplyRate,
    double CloseRate,
    int TodayCap,
    int TodaySent,
    string InstanceStatus,
    bool SendingEnabled);

public record GlobalMetrics(
    int TotalLeads,
    int LeadsToday,
    int LeadsSent7d,
    int LeadsReplied7d,
    int LeadsClosed7d,
    IReadOnlyDictionary<string, int> LeadsByProduct,
    IReadOnlyDictionary<string, int> LeadsBySource,
    IReadOnlyList<SellerMetricRow> Sellers);

public record SellerDashboard(
    SellerMetricRow Metrics,
    IReadOnlyList<LeadDto> ActiveLeads,
    int QueuedCount,
    int TodaySentCount,
    int TodayCap);
