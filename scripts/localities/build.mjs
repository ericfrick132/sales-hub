#!/usr/bin/env node
// Bajada de polígonos ADM2 (departamentos/municipios) de GeoBoundaries para
// LATAM, cálculo de centroides y emisión de:
//   - frontend/public/data/localities-latam.geojson  (lo que carga el mapa)
//   - scripts/localities/localities-import.json      (payload para POST al backend)
//
// Uso:
//   node scripts/localities/build.mjs
//
// Después: node scripts/localities/import.mjs   (postea a la API)
//
// Source: https://www.geoboundaries.org (open license, sin registro).

import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', '..');

// País → (código ISO3 que usa GeoBoundaries, nombre legible).
// Comentá los que no necesités para acelerar el build.
const COUNTRIES = [
  ['ARG', 'AR', 'Argentina'],
  ['BRA', 'BR', 'Brasil'],
  ['CHL', 'CL', 'Chile'],
  ['URY', 'UY', 'Uruguay'],
  ['PRY', 'PY', 'Paraguay'],
  ['BOL', 'BO', 'Bolivia'],
  ['PER', 'PE', 'Perú'],
  ['ECU', 'EC', 'Ecuador'],
  ['COL', 'CO', 'Colombia'],
  ['VEN', 'VE', 'Venezuela'],
  ['MEX', 'MX', 'México'],
  ['CRI', 'CR', 'Costa Rica'],
  ['PAN', 'PA', 'Panamá'],
  ['GTM', 'GT', 'Guatemala'],
  ['HND', 'HN', 'Honduras'],
  ['SLV', 'SV', 'El Salvador'],
  ['NIC', 'NI', 'Nicaragua'],
  ['DOM', 'DO', 'Rep. Dominicana']
];

const ADM_LEVEL = 'ADM2';

async function fetchCountry(iso3) {
  // GeoBoundaries API → metadata con gjDownloadURL apuntando al GeoJSON simplificado.
  const metaUrl = `https://www.geoboundaries.org/api/current/gbOpen/${iso3}/${ADM_LEVEL}/`;
  const meta = await (await fetch(metaUrl)).json();
  if (!meta.simplifiedGeometryGeoJSON && !meta.gjDownloadURL) {
    throw new Error(`Sin GeoJSON disponible para ${iso3}/${ADM_LEVEL}`);
  }
  const gjUrl = meta.simplifiedGeometryGeoJSON || meta.gjDownloadURL;
  const gj = await (await fetch(gjUrl)).json();
  return { meta, gj };
}

// Centroide simple (promedio de coords). No es centroide geométrico exacto pero
// es suficiente para "zoom-to-locality". Soporta Polygon y MultiPolygon.
function centroidOf(geometry) {
  let sumLng = 0, sumLat = 0, n = 0;
  function walk(coords, depth) {
    if (depth === 0) { sumLng += coords[0]; sumLat += coords[1]; n++; return; }
    for (const c of coords) walk(c, depth - 1);
  }
  if (geometry.type === 'Polygon') walk(geometry.coordinates, 2);
  else if (geometry.type === 'MultiPolygon') walk(geometry.coordinates, 3);
  else throw new Error(`Geometría no soportada: ${geometry.type}`);
  return [sumLng / n, sumLat / n];
}

async function main() {
  const allFeatures = [];
  const importItems = [];

  for (const [iso3, iso2, countryName] of COUNTRIES) {
    process.stdout.write(`→ ${iso3} ${countryName}… `);
    try {
      const { gj } = await fetchCountry(iso3);
      let count = 0;
      for (const f of gj.features) {
        const props = f.properties || {};
        // GeoBoundaries usa shapeID para el ID estable y shapeName para el nombre.
        const gid2 = props.shapeID || props.shapeISO || `${iso3}-${count}`;
        const name = props.shapeName || 'Unknown';
        // GeoBoundaries ADM2 no siempre tiene padre ADM1 en el feature, así que
        // dejamos admin1 en blanco si falta — se puede enriquecer después.
        const adm1Name = props.shapeISO ? props.shapeISO.split('-')[1] || '' : '';
        const adm1Gid = props.shapeISO || '';

        const [lng, lat] = centroidOf(f.geometry);

        // Reescribimos el id del feature para que el frontend pueda matchear
        // contra los gid2 que devuelve la API.
        f.id = gid2;
        f.properties = {
          gid2,
          name,
          adm1Name,
          countryCode: iso2,
          countryName
        };
        allFeatures.push(f);

        importItems.push({
          gid2,
          name,
          adminLevel1Gid: adm1Gid,
          adminLevel1Name: adm1Name,
          countryCode: iso2,
          countryName,
          centroidLat: lat,
          centroidLng: lng
        });
        count++;
      }
      console.log(`${count} localities`);
    } catch (err) {
      console.error(`FALLÓ: ${err.message}`);
    }
  }

  const outDir = path.join(ROOT, 'frontend', 'public', 'data');
  const outImport = path.join(__dirname, 'localities-import.json');
  await fs.mkdir(outDir, { recursive: true });

  // Per-country files. El frontend descarga solo los de los países donde
  // hay productos activos en vez del LATAM entero (~91 MB → 1-20 MB por país).
  const buckets = new Map();
  for (const f of allFeatures) {
    const cc = (f.properties?.countryCode || '').toLowerCase();
    if (!cc) continue;
    if (!buckets.has(cc)) buckets.set(cc, []);
    buckets.get(cc).push(f);
  }
  for (const [cc, features] of buckets) {
    const out = path.join(outDir, `localities-${cc}.geojson`);
    await fs.writeFile(out, JSON.stringify({ type: 'FeatureCollection', features }));
    console.log(`✓ ${cc}: ${features.length} features → ${path.relative(ROOT, out)}`);
  }

  await fs.writeFile(outImport, JSON.stringify({ items: importItems }));
  console.log(`\n✓ ${allFeatures.length} features en ${buckets.size} países`);
  console.log(`✓ ${importItems.length} items     → ${path.relative(ROOT, outImport)}`);
  console.log(`\nSiguiente: node scripts/localities/import.mjs`);
}

main().catch(err => { console.error(err); process.exit(1); });
