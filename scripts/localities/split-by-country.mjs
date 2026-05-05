#!/usr/bin/env node
// Toma el localities-latam.geojson grande y emite un archivo por país
// (`localities-{cc}.geojson`, en lowercase ISO2). Pensado para que el frontend
// descargue solo los países donde tiene zonas asignadas, en vez del paquete
// LATAM entero (~91 MB → ~5–20 MB por país).

import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', '..');
const SRC = path.join(ROOT, 'frontend', 'public', 'data', 'localities-latam.geojson');
const OUT_DIR = path.join(ROOT, 'frontend', 'public', 'data');

const raw = await fs.readFile(SRC, 'utf8');
const fc = JSON.parse(raw);

const buckets = new Map();
for (const f of fc.features) {
  const cc = (f.properties?.countryCode || '').toLowerCase();
  if (!cc) continue;
  if (!buckets.has(cc)) buckets.set(cc, []);
  buckets.get(cc).push(f);
}

let total = 0;
for (const [cc, features] of buckets) {
  const out = path.join(OUT_DIR, `localities-${cc}.geojson`);
  const body = JSON.stringify({ type: 'FeatureCollection', features });
  await fs.writeFile(out, body);
  console.log(`✓ ${cc}: ${features.length.toString().padStart(5)} features → ${(body.length / 1_000_000).toFixed(1)} MB`);
  total += features.length;
}
console.log(`\n${total} features distribuidas en ${buckets.size} archivos.`);
