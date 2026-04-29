#!/usr/bin/env node
// Postea localities-import.json al backend en batches.
// Asume que tenés un usuario admin con su JWT en SALESHUB_ADMIN_TOKEN.
//
// Uso:
//   SALESHUB_API=http://localhost:8080 SALESHUB_ADMIN_TOKEN=xxx node scripts/localities/import.mjs

import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const API = process.env.SALESHUB_API || 'http://localhost:8080';
const TOKEN = process.env.SALESHUB_ADMIN_TOKEN;
if (!TOKEN) { console.error('Falta SALESHUB_ADMIN_TOKEN'); process.exit(1); }

const BATCH = 1000;

const file = path.join(__dirname, 'localities-import.json');
const data = JSON.parse(await fs.readFile(file, 'utf-8'));
const items = data.items || [];
console.log(`${items.length} items, batches de ${BATCH}`);

let done = 0;
for (let i = 0; i < items.length; i += BATCH) {
  const slice = items.slice(i, i + BATCH);
  const res = await fetch(`${API}/api/admin/localities/import`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${TOKEN}`
    },
    body: JSON.stringify({ items: slice })
  });
  if (!res.ok) {
    console.error(`Falló batch en offset ${i}: ${res.status} ${await res.text()}`);
    process.exit(1);
  }
  const r = await res.json();
  done += slice.length;
  console.log(`  ${done}/${items.length} (+${r.inserted} new, ~${r.updated} upd)`);
}
console.log('✓ done');
