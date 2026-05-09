import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import maplibregl, { Map as MlMap } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { api } from '../lib/api';
import { fetchCountriesGeoJson } from '../lib/geojson-cache';

type LocalityDto = {
  gid2: string;
  name: string;
  adminLevel1Name: string;
  countryCode: string;
  centroidLat: number;
  centroidLng: number;
};

/// Mapa "Mis Zonas" para el vendedor: muestra solo las localidades asignadas,
/// pintadas en verde, y centra el viewport para que entren todas. No es editable.
export default function MyZonesMap() {
  const myZonesQ = useQuery({
    queryKey: ['my-localities'],
    queryFn: async () => (await api.get<LocalityDto[]>('/sellers/me/localities')).data
  });

  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MlMap | null>(null);
  const [mapLoaded, setMapLoaded] = useState(false);
  const [mapReady, setMapReady] = useState(false);
  const [geojsonMissing, setGeojsonMissing] = useState(false);

  const zones = myZonesQ.data ?? [];
  const gid2Set = useMemo(() => new Set(zones.map(z => z.gid2)), [zones]);

  // País(es) donde tengo zonas. Casi siempre va a ser uno solo (~5 MB).
  const countryCodes = useMemo(() => {
    return Array.from(new Set(zones.map(z => z.countryCode.toLowerCase())));
  }, [zones]);

  // Init mapa una sola vez.
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

  // Cargar geojson de los países donde tengo zonas, agregar layer y filtrar a mis gid2s.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapLoaded || countryCodes.length === 0) return;
    let cancelled = false;
    (async () => {
      try {
        const gj = await fetchCountriesGeoJson(countryCodes);
        if (cancelled) return;
        if (!map.getSource('localities')) {
          map.addSource('localities', { type: 'geojson', data: gj as never, generateId: false });
          map.addLayer({
            id: 'localities-fill', type: 'fill', source: 'localities',
            paint: { 'fill-color': '#16a34a', 'fill-opacity': 0.55 },
            filter: ['in', ['get', 'gid2'], ['literal', []]]
          });
          map.addLayer({
            id: 'localities-outline', type: 'line', source: 'localities',
            paint: { 'line-color': '#15803d', 'line-width': 1.2 },
            filter: ['in', ['get', 'gid2'], ['literal', []]]
          });
        } else {
          (map.getSource('localities') as maplibregl.GeoJSONSource).setData(gj as never);
        }
        setMapReady(true);
      } catch (err) {
        console.warn('No se pudo cargar geojson:', err);
        setGeojsonMissing(true);
      }
    })();
    return () => { cancelled = true; };
  }, [mapLoaded, countryCodes]);

  // Cuando ya tengo el layer + las zonas, ajusto el filtro y hago fitBounds.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    const ids = Array.from(gid2Set);
    map.setFilter('localities-fill', ['in', ['get', 'gid2'], ['literal', ids]]);
    map.setFilter('localities-outline', ['in', ['get', 'gid2'], ['literal', ids]]);
    // Filtrar zonas con centroide inválido (NaN, null o (0,0) que en localidades
    // LATAM siempre indica un row sin geocode). Si no quedan válidas, dejamos
    // el viewport como estaba en vez de tirar maplibre con LngLat NaN.
    const valid = zones.filter(z =>
      Number.isFinite(z.centroidLat) && Number.isFinite(z.centroidLng)
      && !(z.centroidLat === 0 && z.centroidLng === 0));
    if (valid.length > 0) {
      const bounds = new maplibregl.LngLatBounds();
      valid.forEach(z => bounds.extend([z.centroidLng, z.centroidLat]));
      map.fitBounds(bounds, { padding: 40, duration: 600, maxZoom: 11 });
    }
  }, [mapReady, zones, gid2Set]);

  if (myZonesQ.isLoading) {
    return <div className="card p-4 text-sm text-slate-500">Cargando zonas…</div>;
  }
  if (zones.length === 0) {
    return (
      <div className="card p-4 text-sm text-slate-600">
        Aún no tenés zonas asignadas. Pedile a un admin que te asigne territorio en el mapa de zonas.
      </div>
    );
  }

  return (
    <div className="card p-4 space-y-2">
      <div className="flex items-center justify-between flex-wrap gap-2">
        <div>
          <div className="font-semibold">Mis zonas</div>
          <div className="text-xs text-slate-500">
            <b>{zones.length}</b> localidad{zones.length === 1 ? '' : 'es'} asignada{zones.length === 1 ? '' : 's'}
          </div>
        </div>
      </div>
      <div className="relative" style={{ height: 360 }}>
        <div ref={containerRef} className="w-full h-full rounded-md overflow-hidden border border-slate-200" />
        {geojsonMissing && (
          <div className="absolute inset-0 grid place-items-center bg-white/85 rounded-md">
            <div className="text-center text-sm text-slate-600 max-w-md p-6">
              No se pudo cargar el mapa base.
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
