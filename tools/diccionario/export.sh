#!/usr/bin/env bash
# Baja de producción los chats de nuestras líneas (Evolution) y los mensajes + leads de sales-hub
# a la carpeta de datos (fuera de git). Lo llama `backtest.py --refresh`.
set -euo pipefail
DATA="${DICCIONARIO_DATA:-$(cd "$(dirname "$0")/../.." && pwd)/docs/conversaciones/v2}"
HOST=root@64.227.3.140
HUB="host=dbaas-db-3146727-do-user-23398434-0.e.db.ondigitalocean.com port=25060 user=saleshub dbname=saleshub sslmode=require"
cd "$DATA"

cat > /tmp/export_evo.sql <<'SQL'
\copy (select i.name as instance, m.key->>'remoteJid' as jid, coalesce(m.key->>'remoteJidAlt', m.key->>'senderPn') as jid_alt, (m.key->>'fromMe')::boolean as from_me, m.key->>'id' as msg_id, m."messageTimestamp" as ts, m."pushName" as push_name, m."messageType" as mtype, coalesce(m.message->>'conversation', m.message->'extendedTextMessage'->>'text', m.message->'imageMessage'->>'caption', m.message->'videoMessage'->>'caption') as text from "Message" m join "Instance" i on i.id = m."instanceId" where (i.name like 'seller\_%' or i.name like 'app\_%' or i.name like 'tel\_%' or i.name in ('Eric Frick','archicloud')) and m.key->>'remoteJid' not like '%@g.us' and m.key->>'remoteJid' not like '%broadcast%' and m.key->>'remoteJid' not like '%@newsletter') to stdout with csv header
SQL
scp -q /tmp/export_evo.sql "$HOST:/tmp/export_evo.sql"
ssh "$HOST" "docker cp /tmp/export_evo.sql evolution-postgres:/tmp/export_evo.sql && docker exec evolution-postgres sh -c 'psql -U evolution -d evolution -f /tmp/export_evo.sql | gzip' > /tmp/evo.csv.gz"
# La password de la DB de sales-hub se toma del .env del droplet (no queda en el repo).
ssh "$HOST" "P=\$(grep -oP 'Password=\K[^;\"]+' /opt/sales-hub/.env | head -1); \
  docker exec evolution-postgres sh -c \"psql '$HUB password='\$P -c '\\\\copy (select m.lead_id, m.direction, m.timestamp, m.text, m.evolution_instance, m.whatsapp_message_id from conversation_messages m) to stdout with csv header' | gzip\" > /tmp/hub_msgs.csv.gz && \
  docker exec evolution-postgres sh -c \"psql '$HUB password='\$P -c '\\\\copy (select * from leads) to stdout with csv header' | gzip\" > /tmp/hub_leads.csv.gz"
scp -q "$HOST:/tmp/evo.csv.gz" "$HOST:/tmp/hub_msgs.csv.gz" "$HOST:/tmp/hub_leads.csv.gz" .
ls -la evo.csv.gz hub_msgs.csv.gz hub_leads.csv.gz
