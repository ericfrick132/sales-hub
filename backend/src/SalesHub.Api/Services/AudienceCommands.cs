using System.Text.Json;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Services;

/// <summary>
/// Comandos de consola para la tabla <c>audience_leads</c> (lo mismo que hace la pantalla Marketing →
/// Competencia → Públicos, para cargas grandes desde el droplet). Corren dentro del contenedor de la
/// API con su config, sin levantar el server ni los workers:
///   dotnet SalesHub.Api.dll audience-import /tmp/audience.json
///   dotnet SalesHub.Api.dll audience-places [--dataset crossfy-activos] [--max 200] [--no-google] [--all]
/// --all busca también lo que todavía no pasó por la extensión (Instagram → web).
/// </summary>
public static class AudienceCommands
{
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0] is "audience-import" or "audience-places";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AudienceLeadService>();
        if (args[0] == "audience-import")
        {
            var rows = JsonSerializer.Deserialize<List<AudienceRow>>(await File.ReadAllTextAsync(args[1]),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var (ins, upd) = await svc.ImportAsync(rows);
            Console.WriteLine($"audience-import: {rows.Count} filas, {ins} nuevas, {upd} actualizadas");
            return 0;
        }

        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var r = await svc.PlacesAsync(Opt("--dataset"), int.TryParse(Opt("--max"), out var m) ? m : 100,
            useGoogle: !args.Contains("--no-google"), onlyFinderDone: !args.Contains("--all"));
        Console.WriteLine($"audience-places: {r.Pending} pendientes; {r.FromLeads} desde leads, " +
                          $"{r.FromGoogle} desde Google ({r.Searched} búsquedas)");
        return 0;
    }
}
