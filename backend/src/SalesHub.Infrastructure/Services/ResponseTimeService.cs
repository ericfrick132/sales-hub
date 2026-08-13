using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Tiempos de atención sobre conversation_messages. Va en SQL crudo a propósito: la
/// métrica necesita window functions (LAG para detectar el arranque de cada ráfaga del
/// lead) y traer los 30k mensajes a memoria para hacerlo en LINQ sería absurdo.
/// </summary>
public class ResponseTimeService : IResponseTimeService
{
    private readonly ApplicationDbContext _db;

    public ResponseTimeService(ApplicationDbContext db) => _db = db;

    /// <summary>
    /// Margen hacia atrás para que el LAG vea el mensaje anterior al borde de la ventana.
    /// Sin esto, el primer inbound de la ventana se contaría como arranque de ráfaga aunque
    /// venga pegado a otro inbound de justo antes. Una ráfaga real dura minutos, no días.
    /// </summary>
    private static readonly TimeSpan LagMargin = TimeSpan.FromDays(7);

    private const string TurnsSql = """
        WITH m AS (
            SELECT c.lead_id, c."timestamp" AS ts, c.direction,
                   LAG(c.direction) OVER (PARTITION BY c.lead_id ORDER BY c."timestamp", c.id) AS prev_dir
            FROM conversation_messages c
            WHERE c."timestamp" >= @lag_since
        ),
        starts AS (
            SELECT lead_id, ts FROM m
            WHERE direction = 1 AND prev_dir IS DISTINCT FROM 1 AND ts >= @since
        )
        SELECT s.lead_id,
               s.ts,
               (SELECT MIN(o."timestamp") FROM conversation_messages o
                 WHERE o.lead_id = s.lead_id AND o.direction = 0 AND o."timestamp" > s.ts) AS out_ts,
               l.product_key,
               l.seller_id,
               l.source,
               (l.bot_muted_at IS NOT NULL) AS bot_muted
        FROM starts s
        JOIN leads l ON l.id = s.lead_id
        WHERE (@seller_id IS NULL OR l.seller_id = @seller_id)
        ORDER BY s.ts
        """;

    public async Task<IReadOnlyList<ResponseTurn>> GetTurnsAsync(
        DateTimeOffset since, Guid? sellerId = null, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(TurnsSql, ct);
        cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = since });
        cmd.Parameters.Add(new NpgsqlParameter("lag_since", NpgsqlDbType.TimestampTz) { Value = since - LagMargin });
        cmd.Parameters.Add(UuidParam("seller_id", sellerId));

        var rows = new List<ResponseTurn>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var inAt = r.GetFieldValue<DateTimeOffset>(1);
            DateTimeOffset? outAt = r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2);
            rows.Add(new ResponseTurn(
                LeadId: r.GetGuid(0),
                InAt: inAt,
                OutAt: outAt,
                Minutes: outAt is null ? null : (outAt.Value - inAt).TotalMinutes,
                ProductKey: r.IsDBNull(3) ? "" : r.GetString(3),
                SellerId: r.IsDBNull(4) ? null : r.GetGuid(4),
                Source: r.GetInt32(5),
                BotMuted: r.GetBoolean(6)));
        }
        return rows;
    }

    private const string WaitingSql = """
        WITH lo AS (
            SELECT lead_id, MAX("timestamp") AS out_ts
            FROM conversation_messages WHERE direction = 0 GROUP BY lead_id
        ),
        w AS (
            SELECT c.lead_id,
                   MIN(c."timestamp") AS waiting_since,
                   MAX(c."timestamp") AS last_in,
                   COUNT(*) AS pending
            FROM conversation_messages c
            LEFT JOIN lo ON lo.lead_id = c.lead_id
            WHERE c.direction = 1 AND (lo.out_ts IS NULL OR c."timestamp" > lo.out_ts)
            GROUP BY c.lead_id
        )
        SELECT w.lead_id,
               COALESCE(NULLIF(l.name, ''), '(sin nombre)') AS lead_name,
               COALESCE(l.whatsapp_phone, '') AS phone,
               l.product_key,
               l.seller_id,
               l.source,
               w.waiting_since,
               w.last_in,
               w.pending,
               LEFT(COALESCE((SELECT c2.text FROM conversation_messages c2
                               WHERE c2.lead_id = w.lead_id
                               ORDER BY c2."timestamp" DESC, c2.id DESC LIMIT 1), ''), 200) AS last_text,
               (l.bot_muted_at IS NOT NULL) AS bot_muted,
               l.status,
               l.sla_alerted_at
        FROM w
        JOIN leads l ON l.id = w.lead_id
        WHERE (@seller_id IS NULL OR l.seller_id = @seller_id)
          AND (@max_age <= 0 OR w.waiting_since >= NOW() - make_interval(hours => @max_age))
        ORDER BY w.waiting_since
        LIMIT @lim
        """;

    public async Task<IReadOnlyList<WaitingChat>> GetWaitingAsync(
        Guid? sellerId = null, int maxAgeHours = 0, int limit = 200, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(WaitingSql, ct);
        cmd.Parameters.Add(UuidParam("seller_id", sellerId));
        cmd.Parameters.Add(new NpgsqlParameter("max_age", NpgsqlDbType.Integer) { Value = maxAgeHours });
        cmd.Parameters.Add(new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = Math.Clamp(limit, 1, 1000) });

        var rows = new List<WaitingChat>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new WaitingChat(
                LeadId: r.GetGuid(0),
                LeadName: r.GetString(1),
                Phone: r.GetString(2),
                ProductKey: r.IsDBNull(3) ? "" : r.GetString(3),
                SellerId: r.IsDBNull(4) ? null : r.GetGuid(4),
                Source: r.GetInt32(5),
                WaitingSince: r.GetFieldValue<DateTimeOffset>(6),
                LastInAt: r.GetFieldValue<DateTimeOffset>(7),
                PendingMessages: (int)r.GetInt64(8),
                LastText: r.IsDBNull(9) ? "" : r.GetString(9),
                BotMuted: r.GetBoolean(10),
                Status: r.GetInt32(11),
                SlaAlertedAt: r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12)));
        }
        return rows;
    }

    private async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private static NpgsqlParameter UuidParam(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value };
}
