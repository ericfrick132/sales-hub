using SalesHub.Core.Domain.Enums;

namespace SalesHub.Api.Dtos;

/// <summary>Objetivo tal como lo ve/edita el superadmin.</summary>
public record SellerGoalDto(
    Guid Id,
    Guid? SellerId,
    string? SellerName,
    string? ProductKey,
    string Metric,
    string Period,
    int Target,
    bool IsActive);

public record CreateSellerGoalRequest(
    Guid? SellerId,
    string? ProductKey,
    SellerGoalMetric Metric,
    SellerGoalPeriod Period,
    int Target,
    bool IsActive = true);

public record UpdateSellerGoalRequest(
    SellerGoalMetric? Metric,
    SellerGoalPeriod? Period,
    int? Target,
    bool? IsActive);

/// <summary>Objetivo + progreso real, tal como lo ve el vendedor (barra que se carga).</summary>
public record SellerGoalProgressDto(
    Guid GoalId,
    string Metric,
    string Period,
    string? ProductKey,
    int Target,
    int Current,
    int Percent);
