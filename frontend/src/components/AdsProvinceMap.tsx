// Mapa coroplético de Argentina por provincia para el mapa de anuncios (Marketing → Mapa de anuncios).
// GeoJSON: límites provinciales del IGN (CC BY 4.0), simplificado a ~200 KB en /data/provincias-ar.geojson.
import { useEffect, useMemo, useState } from 'react';
import { CircleMarker, GeoJSON, MapContainer, TileLayer, Tooltip, useMap } from 'react-leaflet';
import type { Layer, LeafletMouseEvent, PathOptions } from 'leaflet';
import type { FeatureCollection, Geometry } from 'geojson';
import 'leaflet/dist/leaflet.css';
import { fetchCachedJson } from '../lib/geojson-cache';

/** "Ciudad Autónoma de Buenos Aires" → "ciudad autonoma de buenos aires" (Meta manda tildes inconsistentes). */
export const regionKey = (s: string) =>
  s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();

// Rampa secuencial de un solo tono (claro = poco, oscuro = mucho) + gris para "sin datos".
export const RAMP = ['#cde2fb', '#86b6ef', '#3987e5', '#1c5cab', '#0d366b'];
export const NO_DATA = '#f0efec';

export type Legend = { breaks: number[]; min: number; max: number } | null;

/** Cortes por cuantiles (5 clases) sobre los valores con dato: así Buenos Aires no aplasta al resto. */
export function quantileBreaks(values: number[]): Legend {
  const v = values.filter((x) => Number.isFinite(x)).sort((a, b) => a - b);
  if (v.length === 0) return null;
  const q = (p: number) => v[Math.min(v.length - 1, Math.floor(p * v.length))];
  const breaks = [q(0.2), q(0.4), q(0.6), q(0.8)];
  return { breaks, min: v[0], max: v[v.length - 1] };
}

export function colorFor(value: number | null | undefined, legend: Legend) {
  if (value == null || !Number.isFinite(value) || !legend) return NO_DATA;
  if (legend.min === legend.max) return RAMP[2];
  let i = 0;
  while (i < legend.breaks.length && value > legend.breaks[i]) i++;
  return RAMP[i];
}

type FC = FeatureCollection<Geometry, { name: string; code: string }>;

const AR_CENTER: [number, number] = [-38.8, -64.5];
const CABA_CENTER: [number, number] = [-34.612, -58.445];

function ZoomControls({ amba }: { amba: boolean }) {
  const map = useMap();
  useEffect(() => {
    if (amba) map.flyTo([-34.65, -58.6], 9, { duration: 0.6 });
    else map.flyTo(AR_CENTER, map.getSize().x < 500 ? 3 : 4, { duration: 0.6 });
  }, [amba, map]);
  return null;
}

export default function AdsProvinceMap({
  valueOf,
  tooltipOf,
  selected,
  onToggle,
  legend,
  legendTitle,
  legendFormat,
  legendNote,
  version,
}: {
  /** Valor de la métrica elegida para una provincia (por nombre canónico); null = sin dato. */
  valueOf: (province: string) => number | null;
  /** HTML del tooltip de hover. */
  tooltipOf: (province: string) => string;
  selected: Set<string>;
  onToggle: (province: string) => void;
  legend: Legend;
  legendTitle: string;
  legendFormat: (n: number) => string;
  legendNote?: string;
  /** Cambia cuando cambian datos/métrica/selección: re-pinta las capas. */
  version: string;
}) {
  const [geo, setGeo] = useState<FC | null>(null);
  const [error, setError] = useState(false);
  const [amba, setAmba] = useState(false);

  useEffect(() => {
    fetchCachedJson<FC>('/data/provincias-ar.geojson').then(setGeo).catch(() => setError(true));
  }, []);

  const styleOf = (name: string): PathOptions => {
    const isSel = selected.has(regionKey(name));
    return {
      fillColor: colorFor(valueOf(name), legend),
      fillOpacity: 0.9,
      color: isSel ? '#0f172a' : '#ffffff',
      weight: isSel ? 2.5 : 1,
    };
  };

  const geoLayer = useMemo(() => {
    if (!geo) return null;
    return (
      <GeoJSON
        key={version}
        data={geo}
        style={(f) => styleOf((f?.properties as { name: string }).name)}
        onEachFeature={(f, layer: Layer) => {
          const name = (f.properties as { name: string }).name;
          layer.bindTooltip(tooltipOf(name), { sticky: true, direction: 'top', className: 'ads-map-tip', opacity: 1 });
          layer.on({
            mouseover: (e: LeafletMouseEvent) => e.target.setStyle({ weight: 2.5, color: '#0f172a' }),
            mouseout: (e: LeafletMouseEvent) => e.target.setStyle(styleOf(name)),
            click: () => onToggle(name),
          });
        }}
      />
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [geo, version]);

  const cabaName = 'Ciudad Autónoma de Buenos Aires';
  const cabaSel = selected.has(regionKey(cabaName));

  return (
    <div className="relative h-[380px] md:h-[560px] rounded-lg overflow-hidden border border-slate-200 bg-[#f8fafc]">
      {error && <div className="absolute inset-0 grid place-items-center text-sm text-red-600">No se pudo cargar el mapa.</div>}
      <MapContainer center={AR_CENTER} zoom={4} minZoom={3} maxZoom={11} zoomSnap={0.5} scrollWheelZoom={false}
        style={{ height: '100%', width: '100%', background: '#f8fafc' }}>
        <TileLayer
          attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> · Límites: <a href="https://www.ign.gob.ar">IGN</a>'
          url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
          opacity={0.35}
        />
        {geoLayer}
        {/* CABA es chiquita: marcador propio para verla y clickearla en el zoom país. */}
        {!amba && (
          <CircleMarker
            key={`caba-${version}`}
            center={CABA_CENTER}
            radius={7}
            pathOptions={{ fillColor: colorFor(valueOf(cabaName), legend), fillOpacity: 1, color: cabaSel ? '#0f172a' : '#ffffff', weight: 2 }}
            eventHandlers={{ click: () => onToggle(cabaName) }}>
            <Tooltip sticky direction="top" className="ads-map-tip" opacity={1}>
              <span dangerouslySetInnerHTML={{ __html: tooltipOf(cabaName) }} />
            </Tooltip>
          </CircleMarker>
        )}
        <ZoomControls amba={amba} />
      </MapContainer>

      <div className="absolute top-2 right-2 z-[1000] flex gap-1">
        <button type="button" onClick={() => setAmba(false)}
          className={`px-2 py-1 text-xs rounded-md border shadow-sm ${!amba ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-700 border-slate-300'}`}>
          Argentina
        </button>
        <button type="button" onClick={() => setAmba(true)}
          className={`px-2 py-1 text-xs rounded-md border shadow-sm ${amba ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-700 border-slate-300'}`}>
          Zoom AMBA
        </button>
      </div>

      {/* Referencias */}
      <div className="absolute bottom-2 left-2 z-[1000] bg-white/95 rounded-md border border-slate-200 shadow-sm p-2 text-[11px] text-slate-700 max-w-[15rem]">
        <div className="font-semibold text-slate-800 mb-1">{legendTitle}</div>
        {legend ? (
          <div className="space-y-0.5">
            {RAMP.map((c, i) => {
              const lo = i === 0 ? legend.min : legend.breaks[i - 1];
              const hi = i === RAMP.length - 1 ? legend.max : legend.breaks[i];
              return (
                <div key={c} className="flex items-center gap-1.5">
                  <span className="inline-block w-4 h-3 rounded-sm border border-white" style={{ background: c }} />
                  <span className="tabular-nums">{legendFormat(lo)} – {legendFormat(hi)}</span>
                </div>
              );
            })}
          </div>
        ) : (
          <div className="text-slate-500">Sin datos para esta métrica.</div>
        )}
        <div className="flex items-center gap-1.5 mt-0.5">
          <span className="inline-block w-4 h-3 rounded-sm border border-slate-300" style={{ background: NO_DATA }} />
          <span>sin datos</span>
        </div>
        {legendNote && <div className="text-slate-500 mt-1 leading-snug">{legendNote}</div>}
      </div>
    </div>
  );
}
