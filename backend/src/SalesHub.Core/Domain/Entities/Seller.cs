using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class Seller
{
    public Guid Id { get; set; }

    public string SellerKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string? GoogleSubject { get; set; }

    public SellerRole Role { get; set; } = SellerRole.Seller;
    public bool IsActive { get; set; } = true;

    public string? WhatsappPhone { get; set; }
    public List<string> VerticalsWhitelist { get; set; } = new();
    public List<string> RegionsAssigned { get; set; } = new();

    public SendMode SendMode { get; set; } = SendMode.Balanced;
    public int DailyCap { get; set; } = 50;
    public int DailyVariancePct { get; set; } = 20;
    public int WarmupDays { get; set; } = 7;
    public DateTimeOffset? WarmupStartedAt { get; set; }
    public int ActiveHoursStart { get; set; } = 10;
    public int ActiveHoursEnd { get; set; } = 21;
    public string Timezone { get; set; } = "America/Argentina/Buenos_Aires";
    public int DelayMinSeconds { get; set; } = 45;
    public int DelayMaxSeconds { get; set; } = 180;
    public int BurstSize { get; set; } = 4;
    public int BurstPauseMinSeconds { get; set; } = 900;
    public int BurstPauseMaxSeconds { get; set; } = 2700;
    public int PreSendTypingMinSeconds { get; set; } = 3;
    public int PreSendTypingMaxSeconds { get; set; } = 8;
    public bool ReadIncomingFirst { get; set; } = true;
    public int SkipDayProbabilityPct { get; set; } = 5;
    public int TypoProbabilityPct { get; set; } = 0;

    public bool SendingEnabled { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    public EvolutionInstance? EvolutionInstance { get; set; }
    public ICollection<Lead> Leads { get; set; } = new List<Lead>();
    public ICollection<MessageOutbox> OutboxItems { get; set; } = new List<MessageOutbox>();
    public ICollection<SellerDailyStats> DailyStats { get; set; } = new List<SellerDailyStats>();
}
