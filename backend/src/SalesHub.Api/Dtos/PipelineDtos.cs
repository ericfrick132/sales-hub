using SalesHub.Core.Domain.Enums;

namespace SalesHub.Api.Dtos;

public record TriggerPipelineRequest(
    string? ProductKey,
    LeadSource[]? Sources,
    string? City,
    string? Province,
    string? Category,
    int? MaxPerSource,
    bool AutoQueue);

public record TriggerPipelineResponse(int LeadsCreated);

public record QrCodeResponse(string? QrBase64, string Status);
