namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Pricing de Claude (USD por 1M tokens). Cache read ≈ 0.1x input, cache write ≈ 1.25x input.
/// Tabla cacheada (junio 2026); si en el futuro cambia, actualizar acá.
/// </summary>
public static class ClaudePricing
{
    private static readonly (string prefix, decimal input, decimal output)[] Table =
    {
        ("claude-fable-5", 10m, 50m),
        ("claude-opus-4", 5m, 25m),
        ("claude-sonnet-4", 3m, 15m),
        ("claude-haiku-4", 1m, 5m),
        ("claude-3-5-sonnet", 3m, 15m),
        ("claude-3-5-haiku", 0.80m, 4m),
        ("claude-3-haiku", 0.25m, 1.25m),
    };

    public static decimal CostUsd(string model, int inputTokens, int outputTokens, int cacheReadTokens, int cacheCreationTokens)
    {
        var (inP, outP) = Resolve(model);
        return (inputTokens * inP
              + cacheReadTokens * inP * 0.1m
              + cacheCreationTokens * inP * 1.25m
              + outputTokens * outP) / 1_000_000m;
    }

    private static (decimal input, decimal output) Resolve(string model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        foreach (var (prefix, input, output) in Table)
            if (m.StartsWith(prefix)) return (input, output);
        return (1m, 5m); // fallback ≈ Haiku 4.5
    }
}
