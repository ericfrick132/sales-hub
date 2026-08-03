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
    List<string> KeywordRules,
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
    string? ConnectedPhoneNumber = null,
    bool AutoArchiveChats = false,
    SellerDeviceDto? Device = null);

/// <summary>Dispositivo Android (bridge) asignado al vendedor — la línea sale por el celu, sin QR de Evolution.</summary>
public record SellerDeviceDto(
    Guid Id,
    string Name,
    string Status,
    bool Online,
    int? BatteryLevel,
    DateTimeOffset? LastHeartbeatAt);

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
    List<string>? KeywordRules,
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
    bool? AutoArchiveChats = null);

public record ToggleSendingRequest(bool Enabled);
