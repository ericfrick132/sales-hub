using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Arca;

/// <summary>
/// Carga el nomenclador de actividades de ARCA (CLAE F.883) en <c>arca_activities</c> desde el CSV
/// versionado en el repo (<c>SalesHub.Api/ArcaSeed/clae_f883.csv</c>, separado por ';', con
/// encabezado codigo;descripcion;grupo;grupo_descripcion;division;division_descripcion;seccion;seccion_descripcion).
///
/// Fuente: diccionario CLAE de datos.produccion.gob.ar (cdn.produccion.gob.ar/cdn-cep/clae_agg.csv),
/// que reproduce el F.883 de la RG AFIP 3537/2013. La RG ARCA 5607/2024 agregaba 15 códigos
/// (cripto, plataformas, creadores de contenido) pero la RG 5612/2024 la abrogó, así que no van.
///
/// Corre al arrancar la API. Es idempotente (upsert por código) y nunca borra: si un código sale
/// del CSV queda en la tabla para no dejar leads apuntando a la nada.
/// </summary>
public static class ArcaNomencladorImporter
{
    public static async Task<int> ImportAsync(ApplicationDbContext db, string csvPath, ILogger log, CancellationToken ct = default)
    {
        if (!File.Exists(csvPath))
        {
            log.LogWarning("ArcaSeed: no existe {Path}, el nomenclador no se carga", csvPath);
            return 0;
        }

        var rows = ParseCsv(await File.ReadAllTextAsync(csvPath, Encoding.UTF8, ct));
        if (rows.Count < 2)
        {
            log.LogWarning("ArcaSeed: {Path} vino vacío", csvPath);
            return 0;
        }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name)
        {
            var i = header.IndexOf(name);
            if (i < 0) throw new InvalidOperationException($"ArcaSeed: falta la columna '{name}'");
            return i;
        }
        int cCode = Col("codigo"), cDesc = Col("descripcion"), cGroup = Col("grupo"), cGroupDesc = Col("grupo_descripcion"),
            cDiv = Col("division"), cDivDesc = Col("division_descripcion"), cSec = Col("seccion"), cSecDesc = Col("seccion_descripcion");

        var existing = await db.ArcaActivities.ToDictionaryAsync(a => a.Code, ct);
        int created = 0, updated = 0;
        foreach (var r in rows.Skip(1))
        {
            if (r.Count <= cSecDesc || string.IsNullOrWhiteSpace(r[cCode])) continue;
            var code = r[cCode].Trim().PadLeft(6, '0');
            var incoming = new ArcaActivity
            {
                Code = code,
                Description = r[cDesc].Trim(),
                GroupCode = r[cGroup].Trim().PadLeft(3, '0'),
                GroupDescription = r[cGroupDesc].Trim(),
                DivisionCode = r[cDiv].Trim().PadLeft(2, '0'),
                DivisionDescription = r[cDivDesc].Trim(),
                Section = r[cSec].Trim(),
                SectionDescription = r[cSecDesc].Trim(),
            };

            if (!existing.TryGetValue(code, out var cur))
            {
                db.ArcaActivities.Add(incoming);
                created++;
            }
            else if (cur.Description != incoming.Description || cur.GroupCode != incoming.GroupCode
                     || cur.GroupDescription != incoming.GroupDescription || cur.DivisionCode != incoming.DivisionCode
                     || cur.DivisionDescription != incoming.DivisionDescription || cur.Section != incoming.Section
                     || cur.SectionDescription != incoming.SectionDescription)
            {
                cur.Description = incoming.Description;
                cur.GroupCode = incoming.GroupCode;
                cur.GroupDescription = incoming.GroupDescription;
                cur.DivisionCode = incoming.DivisionCode;
                cur.DivisionDescription = incoming.DivisionDescription;
                cur.Section = incoming.Section;
                cur.SectionDescription = incoming.SectionDescription;
                cur.UpdatedAt = DateTimeOffset.UtcNow;
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        var total = existing.Count + created;
        log.LogInformation("ArcaSeed: nomenclador CLAE con {Total} actividades (nuevas {Created}, actualizadas {Updated})",
            total, created, updated);
        return total;
    }

    /// <summary>CSV con ';' y comillas dobles (las descripciones pueden traer ';' o comillas).</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        text = text.TrimStart('﻿');

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ';': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>();
                    break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
