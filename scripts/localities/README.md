# Seed de localidades LATAM

Pipeline para poblar la tabla `localities` del backend y el GeoJSON que carga el
mapa del frontend, a partir de [GeoBoundaries](https://www.geoboundaries.org)
(open data, ADM2 = departamentos / municipios / partidos).

## Pasos

```bash
# 1) Bajar GeoJSON de los países y generar dos artifacts:
#    - frontend/public/data/localities-latam.geojson  (carga directa el mapa)
#    - scripts/localities/localities-import.json     (payload para la API)
node scripts/localities/build.mjs

# 2) Importar al backend (necesita un JWT de admin)
SALESHUB_API=http://localhost:8080 \
SALESHUB_ADMIN_TOKEN=eyJhbGciOi... \
node scripts/localities/import.mjs
```

`build.mjs` toma 1-3 minutos (depende del peso de cada país). El GeoJSON
resultante anda entre 20-60 MB sin gzip. Si querés sumar/sacar países, editá la
constante `COUNTRIES` arriba del script.

## Iteración 2 (cuando el GeoJSON pese demasiado)

Migrar a PMTiles servido por CDN:

```
brew install tippecanoe
tippecanoe -o localities-latam.pmtiles \
  --layer=localities --simplification=10 --maximum-zoom=10 \
  frontend/public/data/localities-latam.geojson
```

Y el frontend pasa a usar `pmtiles://...` con `maplibre-gl`.
