import { useEffect, useMemo, useRef, useState } from 'react';
import maplibregl, { Map as MlMap } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { fetchCountriesGeoJson } from '../lib/geojson-cache';

export type CaptureZone = {
  gid2: string;
  name: string;
  adminLevel1Name: string;
  countryCode: string;
  centroidLat: number;
  centroidLng: number;
  newCount: number;
  staleCount: number;
  doneCount: number;
  lastCapturedAt?: string | null;
};

export const ZONE_COLORS = { new: '#2563eb', stale: '#f59e0b', done: '#94a3b8' } as const;

/**
 * Mapa de "Capturar de Maps": las zonas del vendedor pintadas según lo que le queda por
 * capturar (azul = nunca, ámbar = conviene refrescar, gris = al día). Click en una
 * localidad para elegirla.
 */
export default function CaptureZonesMap({ zones, selected, onSelect }: {
  zones: CaptureZone[];
  selected: string | null;
  onSelect: (gid2: string) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MlMap | null>(null);
  const [mapLoaded, setMapLoaded] = useState(false);
  const [layersReady, setLayersReady] = useState(false);
  const [geojsonMissing, setGeojsonMissing] = useState(false);
  const fittedRef = useRef(false);
  // Los handlers del mapa se registran una vez: leen lo último por ref.
  const zonesRef = useRef(new Map<string, CaptureZone>());
  const onSelectRef = useRef(onSelect);
  onSelectRef.current = onSelect;

  const byGid2 = useMemo(() => new Map(zones.map((z) => [z.gid2, z])), [zones]);
  zonesRef.current = byGid2;
  const countryCodes = useMemo(
    () => Array.from(new Set(zones.map((z) => z.countryCode.toLowerCase()))).sort().join(','),
    [zones]
  );

  useEffect(() => {
    if (!containerRef.current || mapRef.current) return;
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: {
        version: 8,
        sources: {
          osm: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, attribution: '© OpenStreetMap' }
        },
        layers: [{ id: 'osm', type: 'raster', source: 'osm' }]
      },
      center: [-63.6, -34.6],
      zoom: 3.5,
      attributionControl: { compact: true }
    });
    map.addControl(new maplibregl.NavigationControl({ visualizePitch: false }), 'top-right');
    mapRef.current = map;
    map.on('load', () => setMapLoaded(true));
    return () => { map.remove(); mapRef.current = null; };
  }, []);

  // Geojson de los países donde tiene zonas + capas + hover y click.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapLoaded || !countryCodes || map.getSource('localities')) return;
    let cancelled = false;
    (async () => {
      try {
        const gj = await fetchCountriesGeoJson(countryCodes.split(','));
        if (cancelled || map.getSource('localities')) return;
        map.addSource('localities', { type: 'geojson', data: gj as never });
        const none = ['in', ['get', 'gid2'], ['literal', []]];
        map.addLayer({ id: 'localities-fill', type: 'fill', source: 'localities', paint: { 'fill-color': ZONE_COLORS.done, 'fill-opacity': 0.5 }, filter: none as never });
        map.addLayer({ id: 'localities-outline', type: 'line', source: 'localities', paint: { 'line-color': '#475569', 'line-width': 0.8 }, filter: none as never });
        map.addLayer({ id: 'localities-selected', type: 'line', source: 'localities', paint: { 'line-color': '#0f172a', 'line-width': 3 }, filter: none as never });

        const popup = new maplibregl.Popup({ closeButton: false, closeOnClick: false, offset: 8 });
        map.on('mousemove', 'localities-fill', (e) => {
          const gid2 = (e.features?.[0]?.properties as { gid2?: string } | undefined)?.gid2;
          const z = gid2 ? zonesRef.current.get(gid2) : undefined;
          if (!z) { popup.remove(); return; }
          map.getCanvas().style.cursor = 'pointer';
          const pending = z.newCount + z.staleCount;
          popup.setLngLat(e.lngLat)
            .setHTML(`<div style="font-size:12px"><b>${escapeHtml(z.name)}</b><br/>${pending > 0 ? `${pending} para capturar` : 'al día'}</div>`)
            .addTo(map);
        });
        map.on('mouseleave', 'localities-fill', () => { map.getCanvas().style.cursor = ''; popup.remove(); });
        map.on('click', 'localities-fill', (e) => {
          const gid2 = (e.features?.[0]?.properties as { gid2?: string } | undefined)?.gid2;
          if (gid2 && zonesRef.current.has(gid2)) onSelectRef.current(gid2);
        });
        setLayersReady(true);
      } catch (err) {
        console.warn('No se pudo cargar geojson:', err);
        setGeojsonMissing(true);
      }
    })();
    return () => { cancelled = true; };
  }, [mapLoaded, countryCodes]);

  // Colores según el estado de cada zona (se repinta cuando cambia el producto o se captura).
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !layersReady) return;
    const ids = zones.map((z) => z.gid2);
    const newIds = zones.filter((z) => z.newCount > 0).map((z) => z.gid2);
    const staleIds = zones.filter((z) => z.newCount === 0 && z.staleCount > 0).map((z) => z.gid2);
    const inZones = ['in', ['get', 'gid2'], ['literal', ids]];
    map.setFilter('localities-fill', inZones as never);
    map.setFilter('localities-outline', inZones as never);
    map.setPaintProperty('localities-fill', 'fill-color', [
      'case',
      ['in', ['get', 'gid2'], ['literal', newIds]], ZONE_COLORS.new,
      ['in', ['get', 'gid2'], ['literal', staleIds]], ZONE_COLORS.stale,
      ZONE_COLORS.done,
    ] as never);

    if (!fittedRef.current) {
      const valid = zones.filter((z) =>
        Number.isFinite(z.centroidLat) && Number.isFinite(z.centroidLng) && !(z.centroidLat === 0 && z.centroidLng === 0));
      if (valid.length > 0) {
        const bounds = new maplibregl.LngLatBounds();
        valid.forEach((z) => bounds.extend([z.centroidLng, z.centroidLat]));
        map.fitBounds(bounds, { padding: 40, duration: 600, maxZoom: 10 });
        fittedRef.current = true;
      }
    }
  }, [layersReady, zones]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !layersReady) return;
    map.setFilter('localities-selected', ['==', ['get', 'gid2'], selected ?? ''] as never);
  }, [layersReady, selected]);

  return (
    <div className="relative w-full h-full">
      <div ref={containerRef} className="w-full h-full rounded-md overflow-hidden border border-slate-200" />
      {geojsonMissing && (
        <div className="absolute inset-0 grid place-items-center bg-white/85 rounded-md text-sm text-slate-600">
          No se pudo cargar el mapa.
        </div>
      )}
    </div>
  );
}

function escapeHtml(s: string) {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]!));
}
