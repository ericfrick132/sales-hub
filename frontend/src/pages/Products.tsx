import { useState, useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import type { MediaAsset, MessageStep, Product, Seller } from '../lib/types';

const EMPTY: Product = {
  id: '', productKey: '', displayName: '', active: true, country: 'AR', countryName: 'Argentina',
  regionCode: 'ar', language: 'es', phonePrefix: '54', categories: [], messageTemplate: '',
  openerTemplate: 'buenas',
  checkoutUrl: '', priceDisplay: '', dailyLimit: 60, triggerHours: [10, 14, 18],
  sendHourStart: 10, sendHourEnd: 20,
  requiresAssistedSale: false,
  googlePlacesDailyLeadCap: 60,
  replyTemplates: [],
  messageSteps: []
};

export default function Products() {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<Product | null>(null);
  const [draft, setDraft] = useState<Product>(EMPTY);

  const productsQ = useQuery({
    queryKey: ['products-admin'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  useEffect(() => { if (selected) setDraft(selected); }, [selected?.id]);

  async function save() {
    try {
      const body = draft;
      if (selected?.id) await api.put(`/products/${selected.id}`, body);
      else await api.post('/products', body);
      toast.success('Guardado');
      qc.invalidateQueries({ queryKey: ['products-admin'] });
      if (!selected?.id) { setDraft(EMPTY); setSelected(null); }
    } catch (err: any) { toast.error(err.response?.data?.error ?? 'Falló'); }
  }

  function onChange<K extends keyof Product>(k: K, v: Product[K]) {
    setDraft((d) => ({ ...d, [k]: v }));
  }

  return (
    <div className="grid grid-cols-1 md:grid-cols-12 gap-4 md:gap-6">
      <div className="md:col-span-4">
        <div className="flex items-center justify-between mb-2">
          <h2 className="text-xl font-bold">Aplicaciones</h2>
          <button className="btn-primary text-xs" onClick={() => { setSelected(null); setDraft(EMPTY); }}>+ Nuevo</button>
        </div>
        <div className="card divide-y divide-slate-100">
          {(productsQ.data ?? []).map((p) => (
            <button key={p.id} onClick={() => setSelected(p)}
              className={`w-full text-left p-3 hover:bg-slate-50 ${selected?.id === p.id ? 'bg-brand-50' : ''}`}>
              <div className="font-medium">{p.displayName}</div>
              <div className="text-xs text-slate-500">{p.productKey} — {p.active ? 'activo' : 'pausado'}</div>
            </button>
          ))}
        </div>
      </div>

      <div className="md:col-span-8 card p-4 md:p-5 space-y-3">
        <h3 className="font-semibold">{selected?.id ? `Editar: ${selected.displayName}` : 'Nuevo producto'}</h3>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label="product_key">
            <input className="input" value={draft.productKey} onChange={(e) => onChange('productKey', e.target.value)} disabled={!!selected?.id} />
          </Field>
          <Field label="Nombre"><input className="input" value={draft.displayName} onChange={(e) => onChange('displayName', e.target.value)} /></Field>
          <Field label="País (code)"><input className="input" value={draft.country} onChange={(e) => onChange('country', e.target.value)} /></Field>
          <Field label="País (nombre)"><input className="input" value={draft.countryName} onChange={(e) => onChange('countryName', e.target.value)} /></Field>
          <Field label="Region code (maps)"><input className="input" value={draft.regionCode} onChange={(e) => onChange('regionCode', e.target.value)} /></Field>
          <Field label="Idioma"><input className="input" value={draft.language} onChange={(e) => onChange('language', e.target.value)} /></Field>
          <Field label="Prefix teléfono"><input className="input" value={draft.phonePrefix} onChange={(e) => onChange('phonePrefix', e.target.value)} /></Field>
          <Field label="Checkout URL"><input className="input" value={draft.checkoutUrl} onChange={(e) => onChange('checkoutUrl', e.target.value)} /></Field>
          <Field label="Precio display"><input className="input" value={draft.priceDisplay} onChange={(e) => onChange('priceDisplay', e.target.value)} /></Field>
          <Field label="Daily limit"><input type="number" className="input" value={draft.dailyLimit} onChange={(e) => onChange('dailyLimit', +e.target.value)} /></Field>
          <Field label="Leads/día Google Places (0 = sin tope)">
            <input type="number" className="input" value={draft.googlePlacesDailyLeadCap}
              onChange={(e) => onChange('googlePlacesDailyLeadCap', +e.target.value)} />
          </Field>
        </div>
        <Field label="Categorías de búsqueda Google Maps (coma)">
          <input className="input" value={draft.categories.join(', ')}
            onChange={(e) => onChange('categories', e.target.value.split(',').map((s) => s.trim()).filter(Boolean))} />
        </Field>
        <Field label="Trigger hours (coma, 0-23)">
          <input className="input" value={draft.triggerHours.join(', ')}
            onChange={(e) => onChange('triggerHours', e.target.value.split(',').map((s) => parseInt(s.trim())).filter((n) => !isNaN(n)))} />
        </Field>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label="Hora inicio envíos (0-23)">
            <input type="number" min={0} max={24} className="input"
              value={draft.sendHourStart}
              onChange={(e) => onChange('sendHourStart', Math.max(0, Math.min(24, +e.target.value)))} />
          </Field>
          <Field label="Hora fin envíos (1-24)">
            <input type="number" min={0} max={24} className="input"
              value={draft.sendHourEnd}
              onChange={(e) => onChange('sendHourEnd', Math.max(0, Math.min(24, +e.target.value)))} />
            <div className="text-xs text-slate-400 mt-1">
              Si inicio ≥ fin, no aplica restricción de producto (queda solo la del vendedor).
            </div>
          </Field>
        </div>
        <StepsEditor
          productKey={selected?.productKey ?? draft.productKey}
          steps={draft.messageSteps ?? []}
          onChange={(steps) => onChange('messageSteps', steps)}
        />
        <Field label="Respuestas rápidas (una por línea)">
          <textarea
            className="input min-h-24 text-sm"
            placeholder={'¿Te interesa que te pase más info?\n¿Cuándo te queda bien una llamada?\nQuedo atento.'}
            value={(draft.replyTemplates ?? []).join('\n')}
            onChange={(e) =>
              onChange(
                'replyTemplates',
                e.target.value.split('\n').map((s) => s.trim()).filter(Boolean)
              )
            }
          />
          <div className="text-xs text-slate-400 mt-1">
            Aparecen como botones arriba del input en la pantalla de chat. Click → llena el campo de respuesta.
          </div>
        </Field>
        <details className="text-xs text-slate-500">
          <summary className="cursor-pointer hover:text-slate-700">Legacy: opener + mensaje único (en desuso, dejar vacío si usás steps)</summary>
          <div className="mt-2 space-y-2 pl-3 border-l-2 border-slate-200">
            <Field label="Opener (legacy, no usar si tenés steps)">
              <textarea className="input min-h-12 font-mono text-xs"
                value={draft.openerTemplate} onChange={(e) => onChange('openerTemplate', e.target.value)} />
            </Field>
            <Field label="Mensaje template legacy">
              <textarea className="input min-h-32 font-mono text-xs"
                value={draft.messageTemplate} onChange={(e) => onChange('messageTemplate', e.target.value)} />
              <div className="text-[11px] text-slate-400 mt-1">
                Si messageSteps está vacío, se usan estos dos (opener + main). Si messageSteps tiene contenido, estos dos se ignoran. Migrá al editor de steps de arriba.
              </div>
            </Field>
          </div>
        </details>
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.active} onChange={(e) => onChange('active', e.target.checked)} />
            Activo
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.requiresAssistedSale} onChange={(e) => onChange('requiresAssistedSale', e.target.checked)} />
            Requiere venta asistida (demo con admin)
          </label>
          <button className="btn-primary ml-auto" onClick={save}>Guardar</button>
        </div>

        {selected?.productKey && (
          <AudioStatsPanel productKey={selected.productKey} />
        )}

        <TestSendPanel
          productId={selected?.id ?? ''}
          defaultPrefix={draft.phonePrefix}
          hasSteps={(draft.messageSteps ?? []).length > 0}
        />
      </div>
    </div>
  );
}

interface AudioStatRow {
  mediaAssetId: string;
  fileName: string;
  mimeType: string;
  sent: number;
  replied: number;
  interested: number;
  demoScheduled: number;
  closed: number;
}

function AudioStatsPanel({ productKey }: { productKey: string }) {
  const statsQ = useQuery({
    queryKey: ['audio-stats', productKey],
    queryFn: async () => (await api.get<AudioStatRow[]>(`/products/${productKey}/audio-stats`)).data,
    refetchInterval: 60_000
  });
  const rows = statsQ.data ?? [];
  if (rows.length === 0) return null;

  const pct = (n: number, d: number) => d === 0 ? '—' : `${((n / d) * 100).toFixed(1)}%`;
  // Best performer = mayor reply rate, con mínimo 5 envíos para evitar ruido.
  const eligible = rows.filter(r => r.sent >= 5);
  const best = eligible.length === 0 ? null
    : eligible.reduce((a, b) => (a.replied / a.sent) >= (b.replied / b.sent) ? a : b);

  return (
    <div className="border-t border-slate-200 pt-4 mt-2 space-y-2">
      <h4 className="font-semibold text-sm flex items-center gap-2">
        <span>Performance de audios</span>
        <span className="text-xs text-slate-400 font-normal">tasa de respuesta y conversión por archivo</span>
      </h4>
      <div className="overflow-x-auto">
        <table className="w-full text-xs">
          <thead className="text-slate-500">
            <tr>
              <th className="text-left py-1 pr-2">Archivo</th>
              <th className="text-right py-1 px-2">Enviados</th>
              <th className="text-right py-1 px-2">Respondieron</th>
              <th className="text-right py-1 px-2">Interesados</th>
              <th className="text-right py-1 px-2">Demos</th>
              <th className="text-right py-1 px-2">Cerrados</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => {
              const isBest = best?.mediaAssetId === r.mediaAssetId;
              return (
                <tr key={r.mediaAssetId} className={`border-t border-slate-100 ${isBest ? 'bg-emerald-50' : ''}`}>
                  <td className="py-1 pr-2">
                    <div className="flex items-center gap-1">
                      {isBest && <span title="Mejor respuesta (≥5 envíos)">🏆</span>}
                      <span className="truncate max-w-[200px]" title={r.fileName}>{r.fileName}</span>
                    </div>
                  </td>
                  <td className="text-right py-1 px-2 font-mono">{r.sent}</td>
                  <td className="text-right py-1 px-2 font-mono">
                    {r.replied} <span className="text-slate-400">({pct(r.replied, r.sent)})</span>
                  </td>
                  <td className="text-right py-1 px-2 font-mono">
                    {r.interested} <span className="text-slate-400">({pct(r.interested, r.sent)})</span>
                  </td>
                  <td className="text-right py-1 px-2 font-mono">{r.demoScheduled}</td>
                  <td className="text-right py-1 px-2 font-mono">{r.closed}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function TestSendPanel({ productId, defaultPrefix, hasSteps }: {
  productId: string;
  defaultPrefix: string;
  hasSteps: boolean;
}) {
  const sellersQ = useQuery({
    queryKey: ['sellers-admin'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });
  const connected = (sellersQ.data ?? []).filter((s) => s.instanceStatus === 'Connected');

  const [sellerId, setSellerId] = useState('');
  const [phone, setPhone] = useState('');
  const [sending, setSending] = useState(false);

  useEffect(() => {
    if (!sellerId && connected.length > 0) setSellerId(connected[0].id);
  }, [connected, sellerId]);

  async function send() {
    if (!productId) return toast.error('Guardá el producto primero');
    if (!hasSteps) return toast.error('El producto no tiene pasos configurados todavía');
    if (!sellerId) return toast.error('Elegí un vendedor con instancia conectada');
    const cleaned = phone.replace(/[^\d]/g, '');
    if (!cleaned) return toast.error('Número inválido (incluí prefijo de país)');

    setSending(true);
    try {
      const { data } = await api.post<{ sentSteps: number; totalSteps: number }>(
        '/test-send/cadence',
        { productId, sellerId, phone: cleaned }
      );
      toast.success(`Enviados ${data.sentSteps}/${data.totalSteps} pasos`);
    } catch (err: any) {
      toast.error(err?.response?.data?.error ?? 'Falló');
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="border-t border-slate-200 pt-4 mt-2 space-y-3">
      <div className="flex items-baseline gap-2 flex-wrap">
        <h4 className="font-semibold text-sm">Probar envío de WhatsApp</h4>
        <span className="text-xs text-slate-400">
          dispara la cadencia configurada arriba contra un número arbitrario
        </span>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <Field label="Vendedor (instancia conectada)">
          <select className="input" value={sellerId} onChange={(e) => setSellerId(e.target.value)}>
            {connected.length === 0 && <option value="">— sin instancias conectadas —</option>}
            {connected.map((s) => (
              <option key={s.id} value={s.id}>{s.displayName} ({s.evolutionInstance})</option>
            ))}
          </select>
        </Field>
        <Field label={`Número destino (con prefijo, ej. ${defaultPrefix}9...)`}>
          <input className="input" value={phone} onChange={(e) => setPhone(e.target.value)}
            placeholder={`${defaultPrefix}9XXXXXXXXX`} />
        </Field>
      </div>

      <div className="text-xs text-slate-500">
        Se envían los pasos en orden con un delay corto fijo (no usa los delays reales del producto).
        Si un paso tiene varias variantes de audio, se manda la <b>primera</b> para que la prueba
        sea determinística.
      </div>

      <button className="btn-primary" onClick={send} disabled={sending || !hasSteps}>
        {sending ? 'Enviando...' : 'Enviar pasos al número'}
      </button>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><div className="text-xs text-slate-500 mb-1">{label}</div>{children}</label>;
}

function StepsEditor({ productKey, steps, onChange }: { productKey: string; steps: MessageStep[]; onChange: (s: MessageStep[]) => void }) {
  const qc = useQueryClient();
  const mediaQ = useQuery({
    queryKey: ['product-media', productKey],
    enabled: !!productKey,
    queryFn: async () => (await api.get<MediaAsset[]>(`/products/${productKey}/media`)).data
  });
  const mediaById = (mediaQ.data ?? []).reduce((acc, m) => { acc[m.id] = m; return acc; }, {} as Record<string, MediaAsset>);

  function update(i: number, patch: Partial<MessageStep>) {
    onChange(steps.map((s, idx) => (idx === i ? { ...s, ...patch } : s)));
  }
  function remove(i: number) {
    onChange(steps.filter((_, idx) => idx !== i));
  }
  function add() {
    onChange([...steps, { text: '', delaySeconds: steps.length === 0 ? 0 : 60 }]);
  }
  function move(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= steps.length) return;
    const next = [...steps];
    [next[i], next[j]] = [next[j], next[i]];
    if (j === 0) next[0] = { ...next[0], delaySeconds: 0 };
    onChange(next);
  }
  async function handleUpload(stepIndex: number, file: File) {
    if (!productKey) {
      toast.error('Guardá el producto primero antes de adjuntar archivos');
      return;
    }
    const fd = new FormData();
    fd.append('file', file);
    try {
      const { data } = await api.post<MediaAsset>(`/products/${productKey}/media`, fd, {
        headers: { 'Content-Type': 'multipart/form-data' }
      });
      qc.invalidateQueries({ queryKey: ['product-media', productKey] });
      // Audios → lista de variantes para A/B testing. Imágenes/PDFs → uno solo.
      if (data.mimeType.startsWith('audio/')) {
        const current = steps[stepIndex];
        const existing = current.mediaAssetIds && current.mediaAssetIds.length > 0
          ? current.mediaAssetIds
          : (current.mediaAssetId ? [current.mediaAssetId] : []);
        update(stepIndex, {
          mediaAssetIds: [...existing, data.id],
          mediaAssetId: null
        });
      } else {
        update(stepIndex, { mediaAssetId: data.id, mediaAssetIds: [] });
      }
      toast.success(`Subido: ${data.fileName}`);
    } catch (err: any) {
      toast.error(err?.response?.data?.error ?? 'Falló la subida');
    }
  }

  function removeVariant(stepIndex: number, assetId: string) {
    const s = steps[stepIndex];
    const list = (s.mediaAssetIds ?? []).filter(id => id !== assetId);
    update(stepIndex, {
      mediaAssetIds: list,
      // Si vaciamos la lista y había un legacy, también lo limpiamos.
      mediaAssetId: s.mediaAssetId === assetId ? null : s.mediaAssetId
    });
  }
  function clearStepMedia(stepIndex: number) {
    update(stepIndex, { mediaAssetId: null, mediaAssetIds: [] });
  }

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <div>
          <div className="text-sm font-medium">Cadencia de mensajes</div>
          <div className="text-xs text-slate-500">
            Se mandan en orden. Si el lead responde, los siguientes se cancelan.
            Soporta <code>&#123;name&#125;</code>, <code>&#123;city&#125;</code>, <code>&#123;category&#125;</code>, etc. y spin-text.
          </div>
        </div>
        <button type="button" className="btn-secondary text-xs" onClick={add}>+ Paso</button>
      </div>

      {steps.length === 0 && (
        <div className="text-xs text-slate-500 border border-dashed border-slate-300 rounded p-3 text-center">
          Sin pasos. Agregá uno o dejá vacío para usar el opener + mensaje legacy de abajo.
        </div>
      )}

      {steps.map((s, i) => {
        const variantIds = (s.mediaAssetIds && s.mediaAssetIds.length > 0)
          ? s.mediaAssetIds
          : (s.mediaAssetId ? [s.mediaAssetId] : []);
        const variantAssets = variantIds.map(id => mediaById[id]).filter(Boolean);
        const isAudioStep = variantAssets.length > 0 && variantAssets[0].mimeType.startsWith('audio/');
        const firstAsset = variantAssets[0];
        return (
          <div key={i} className="border border-slate-200 rounded-md p-3 bg-slate-50/40 space-y-2">
            <div className="flex items-center gap-2 text-xs">
              <span className="font-semibold text-slate-700">Paso {i + 1}</span>
              {i === 0 ? (
                <span className="text-slate-400">(sale al asignar — delay siempre 0)</span>
              ) : (
                <DelayInput
                  seconds={s.delaySeconds}
                  onChange={(sec) => update(i, { delaySeconds: sec })}
                />
              )}
              <div className="ml-auto flex gap-1">
                <button type="button" className="btn-secondary text-xs px-2 py-0.5"
                  disabled={i === 0} onClick={() => move(i, -1)} title="Subir">↑</button>
                <button type="button" className="btn-secondary text-xs px-2 py-0.5"
                  disabled={i === steps.length - 1} onClick={() => move(i, 1)} title="Bajar">↓</button>
                <button type="button" className="btn-secondary text-xs px-2 py-0.5 text-rose-600"
                  onClick={() => remove(i)} title="Eliminar">×</button>
              </div>
            </div>
            <textarea
              className="input min-h-20 font-mono text-sm w-full"
              placeholder={firstAsset
                ? (isAudioStep
                    ? 'Texto opcional (sale como mensaje separado antes del audio)'
                    : 'Caption opcional (texto que va con el archivo)')
                : (i === 0 ? 'ej. {Hola!|Buenas!} {name}, ...' : 'ej. te dejo el link: {checkout_url}')}
              value={s.text}
              onChange={(e) => update(i, { text: e.target.value })}
            />

            {variantAssets.length === 0 && (
              <EmptyAttachment onPick={(file) => handleUpload(i, file)} />
            )}

            {variantAssets.length > 0 && !isAudioStep && (
              <AttachmentRow
                asset={firstAsset}
                onRemove={() => clearStepMedia(i)}
              />
            )}

            {isAudioStep && (
              <div className="space-y-1">
                <div className="text-[11px] text-slate-500 px-1 flex items-center gap-2">
                  <span>🎤 {variantAssets.length} variante{variantAssets.length === 1 ? '' : 's'} de audio</span>
                  {variantAssets.length > 1 && (
                    <span className="text-emerald-700">· se rotan en round-robin</span>
                  )}
                </div>
                {variantAssets.map((a, idx) => (
                  <AttachmentRow
                    key={a.id}
                    asset={a}
                    label={`Variante ${idx + 1}`}
                    onRemove={() => removeVariant(i, a.id)}
                  />
                ))}
                <AddVariantButton onPick={(file) => handleUpload(i, file)} />
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

function AttachmentRow({ asset, onRemove, label }: {
  asset: MediaAsset;
  onRemove: () => void;
  label?: string;
}) {
  const isImage = asset.mimeType.startsWith('image/');
  const isAudio = asset.mimeType.startsWith('audio/');
  const apiBase = (import.meta.env.VITE_API_URL ?? '/api').replace(/\/$/, '');
  const previewUrl = `${apiBase}/media/${asset.id}`;
  return (
    <div className="flex items-center gap-2 text-xs bg-white border border-slate-200 rounded p-2">
      {isImage ? (
        <img src={previewUrl} alt={asset.fileName} className="w-12 h-12 object-cover rounded border border-slate-200" />
      ) : (
        <div className="w-12 h-12 rounded border border-slate-200 grid place-items-center bg-slate-50 text-slate-400">
          {isAudio ? '🎤' : '📄'}
        </div>
      )}
      <div className="flex-1 min-w-0">
        <div className="font-medium truncate">
          {label && <span className="text-slate-500 mr-1">{label}:</span>}
          {asset.fileName}
        </div>
        <div className="text-slate-500 text-[11px]">
          {asset.mimeType} · {(asset.sizeBytes / 1024).toFixed(0)} KB
        </div>
        {isAudio && (
          <audio controls preload="none" className="w-full mt-1 h-8">
            <source src={previewUrl} type={asset.mimeType} />
          </audio>
        )}
      </div>
      <button type="button" className="btn-secondary text-xs text-rose-600" onClick={onRemove}>Quitar</button>
    </div>
  );
}

function EmptyAttachment({ onPick }: { onPick: (file: File) => void }) {
  const inputId = `att-${Math.random().toString(36).slice(2, 8)}`;
  return (
    <label htmlFor={inputId}
      className="flex items-center gap-2 text-xs border border-dashed border-slate-300 rounded p-2 cursor-pointer hover:bg-slate-50">
      <span className="text-lg">📎</span>
      <span className="flex-1 text-slate-500">Adjuntar audio (nota de voz), imagen o PDF (opcional)</span>
      <input id={inputId} type="file" className="hidden"
        accept="audio/*,image/*,application/pdf"
        onChange={(e) => {
          const f = e.target.files?.[0];
          if (f) onPick(f);
          e.target.value = '';
        }}
      />
    </label>
  );
}

function AddVariantButton({ onPick }: { onPick: (file: File) => void }) {
  const inputId = `var-${Math.random().toString(36).slice(2, 8)}`;
  return (
    <label htmlFor={inputId}
      className="flex items-center justify-center gap-1 text-xs border border-dashed border-slate-300 rounded p-2 cursor-pointer hover:bg-slate-50 text-slate-500">
      <span>+ Agregar otra variante de audio</span>
      <input id={inputId} type="file" className="hidden" accept="audio/*"
        onChange={(e) => {
          const f = e.target.files?.[0];
          if (f) onPick(f);
          e.target.value = '';
        }}
      />
    </label>
  );
}

function DelayInput({ seconds, onChange }: { seconds: number; onChange: (s: number) => void }) {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return (
    <div className="flex items-center gap-1 text-slate-600">
      <span>esperar</span>
      <input type="number" min={0} max={1440}
        className="input w-14 text-xs px-1 py-0.5"
        value={m}
        onChange={(e) => onChange(Math.max(0, +e.target.value) * 60 + s)}
      />
      <span>min</span>
      <input type="number" min={0} max={59}
        className="input w-12 text-xs px-1 py-0.5"
        value={s}
        onChange={(e) => onChange(m * 60 + Math.max(0, Math.min(59, +e.target.value)))}
      />
      <span>seg</span>
    </div>
  );
}
