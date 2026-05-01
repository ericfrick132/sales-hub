using SalesHub.Core.Domain.Enums;

namespace SalesHub.Api.Dtos;

public record SellerDto(
    Guid Id,
    string SellerKey,
    string DisplayName,
    string Email,
    string Role,
    bool IsActive,
    bool SendingEnabled,
    string? WhatsappPhone,
    string? EvolutionInstance,
    InstanceStatus? InstanceStatus,
    List<string> VerticalsWhitelist,
    List<string> RegionsAssigned,
    SendMode SendMode,
    int DailyCap,
    int DailyVariancePct,
    int WarmupDays,
    DateTimeOffset? WarmupStartedAt,
    int ActiveHoursStart,
    int ActiveHoursEnd,
    string Timezone,
    int DelayMinSeconds,
    int DelayMaxSeconds,
    int BurstSize,
    int BurstPauseMinSeconds,
    int BurstPauseMaxSeconds,
    int PreSendTypingMinSeconds,
    int PreSendTypingMaxSeconds,
    bool ReadIncomingFirst,
    int SkipDayProbabilityPct,
    int TypoProbabilityPct,
    List<string> RegionsAssigned);

public record CreateSellerRequest(
    string SellerKey,
    string DisplayName,
    string Email,
    string Password,
    List<string>? VerticalsWhitelist,
    List<string>? RegionsAssigned,
    string? WhatsappPhone,
    SellerRole Role = SellerRole.Seller);

public record UpdateSellerRequest(
    string? DisplayName,
    string? Password,
    bool? IsActive,
    List<string>? VerticalsWhitelist,
    List<string>? RegionsAssigned,
    string? WhatsappPhone,
    SendMode? SendMode,
    int? DailyCap,
    int? DailyVariancePct,
    int? WarmupDays,
    int? ActiveHoursStart,
    int? ActiveHoursEnd,
    string? Timezone,
    int? DelayMinSeconds,
    int? DelayMaxSeconds,
    int? BurstSize,
    int? BurstPauseMinSeconds,
    int? BurstPauseMaxSeconds,
    int? PreSendTypingMinSeconds,
    int? PreSendTypingMaxSeconds,
    bool? ReadIncomingFirst,
    int? SkipDayProbabilityPct,
    int? TypoProbabilityPct,
    List<string>? RegionsAssigned);

public record ToggleSendingRequest(bool Enabled);
