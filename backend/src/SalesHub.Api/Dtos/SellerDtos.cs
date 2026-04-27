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
    int TypoProbabilityPct);

public record CreateSellerRequest(
    string SellerKey,
    string DisplayName,
    string Email,
    string Password,
    List<string>? VerticalsWhitelist,
    string? WhatsappPhone,
    SellerRole Role = SellerRole.Seller);

public record UpdateSellerRequest(
    string? DisplayName,
    string? Password,
    bool? IsActive,
    List<string>? VerticalsWhitelist,
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
    int? TypoProbabilityPct);

public record ToggleSendingRequest(bool Enabled);
