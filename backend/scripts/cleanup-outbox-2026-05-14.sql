-- Limpieza puntual de message_outbox — 2026-05-14
--
-- Contexto: el outbox acumuló filas duplicadas porque /queue y /assign no tenían
-- idempotencia y los leads se re-encolaron varias veces (ver fix en LeadsController).
-- Este script: (1) recupera filas zombie trabadas en Sending, (2) deduplica las
-- filas pendientes (Scheduled) que son copias exactas (mismo lead + mensaje + media).
--
-- Idempotente: correr de nuevo no hace nada extra. Corré DESPUÉS de deployar el
-- backend con los fixes, así no se vuelve a ensuciar mientras limpia.
--
-- Uso (desde el droplet, que está whitelisted en el firewall del cluster):
--   PGPASSWORD=... psql "host=... port=25060 user=saleshub dbname=saleshub sslmode=require" \
--     -f cleanup-outbox-2026-05-14.sql
--
-- status: 0=Scheduled 1=Sending 2=Sent 3=Failed 4=Cancelled

\echo ===== ANTES =====
SELECT status, count(*) FROM message_outbox GROUP BY status ORDER BY status;

BEGIN;

-- (1) Zombies: filas trabadas en Sending (proceso reiniciado mid-envío).
-- Vuelven a Scheduled, o Failed si ya agotaron los 3 intentos.
UPDATE message_outbox
SET status     = CASE WHEN attempts >= 3 THEN 3 ELSE 0 END,
    locked_at  = NULL
WHERE status = 1;

-- (2) Dedup de pendientes: por cada (lead_id, message, media_asset_id) con
-- status=0 repetido, conservamos la fila más vieja y cancelamos el resto.
WITH dups AS (
    SELECT id,
           row_number() OVER (
               PARTITION BY lead_id, message, COALESCE(media_asset_id, '00000000-0000-0000-0000-000000000000'::uuid)
               ORDER BY created_at
           ) AS rn
    FROM message_outbox
    WHERE status = 0
)
UPDATE message_outbox o
SET status = 4,
    error  = 'dedup cleanup 2026-05-14'
FROM dups
WHERE o.id = dups.id
  AND dups.rn > 1;

COMMIT;

\echo ===== DESPUES =====
SELECT status, count(*) FROM message_outbox GROUP BY status ORDER BY status;

-- Diagnóstico opcional: leads que TODAVÍA tienen más de una cadencia pendiente
-- (re-encolados con TEXTO distinto entre tandas — el dedup exacto no los toca).
-- Revisalos a mano si querés dejar una sola cadencia por lead.
\echo ===== LEADS CON >1 CADENCIA PENDIENTE (revisar a mano) =====
SELECT lead_id, count(*) AS filas_scheduled, count(DISTINCT date_trunc('minute', created_at)) AS tandas
FROM message_outbox
WHERE status = 0
GROUP BY lead_id
HAVING count(DISTINCT date_trunc('minute', created_at)) > 1
ORDER BY filas_scheduled DESC
LIMIT 30;
