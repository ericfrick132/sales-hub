import { useState, useEffect, useRef } from 'react';
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

        <TestSendPanel defaultPrefix={draft.phonePrefix} />
      </div>
    </div>
  );
}

function TestSendPanel({ defaultPrefix }: { defaultPrefix: string }) {
  type Kind = 'text' | 'voice' | 'image' | 'document';

  const sellersQ = useQuery({
    queryKey: ['sellers-admin'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });
  const connected = (sellersQ.data ?? []).filter((s) => s.instanceStatus === 'Connected');

  const [sellerId, setSellerId] = useState('');
  const [phone, setPhone] = useState('');
  const [kind, setKind] = useState<Kind>('text');
  const [text, setText] = useState('Mensaje de prueba');
  const [caption, setCaption] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [sending, setSending] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!sellerId && connected.length > 0) setSellerId(connected[0].id);
  }, [connected, sellerId]);
  useEffect(() => {
    setFile(null);
    if (fileInputRef.current) fileInputRef.current.value = '';
  }, [kind]);

  const accept = kind === 'voice' ? 'audio/*'
    : kind === 'image' ? 'image/*'
    : kind === 'document' ? 'application/pdf,application/*' : '';

  async function send() {
    if (!sellerId) return toast.error('Elegí un vendedor con instancia conectada');
    const cleaned = phone.replace(/[^\d]/g, '');
    if (!cleaned) return toast.error('Número inválido (incluí prefijo de país)');
    if (kind === 'text' && !text.trim()) return toast.error('Texto vacío');
    if (kind !== 'text' && !file) return toast.error('Seleccioná un archivo');

    setSending(true);
    try {
      if (kind === 'text') {
        await api.post('/test-send/text', { sellerId, phone: cleaned, text });
      } else if (kind === 'voice') {
        const fd = new FormData();
        fd.append('sellerId', sellerId);
        fd.append('phone', cleaned);
        fd.append('file', file!);
        await api.post('/test-send/voice', fd, { headers: { 'Content-Type': 'multipart/form-data' } });
      } else {
        const fd = new FormData();
        fd.append('sellerId', sellerId);
        fd.append('phone', cleaned);
        fd.append('file', file!);
        if (caption) fd.append('caption', caption);
        await api.post('/test-send/media', fd, { headers: { 'Content-Type': 'multipart/form-data' } });
      }
      toast.success('Enviado');
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
          envía desde la instancia del vendedor a un número arbitrario (no toca leads)
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

      <div className="flex gap-2 flex-wrap">
        {(['text', 'voice', 'image', 'document'] as Kind[]).map((k) => (
          <button key={k} type="button"
            className={`px-3 py-1.5 rounded text-sm ${kind === k ? 'bg-brand-600 text-white' : 'bg-slate-100 hover:bg-slate-200'}`}
            onClick={() => setKind(k)}>
            {k === 'text' ? 'Texto' : k === 'voice' ? 'Audio (nota de voz)' : k === 'image' ? 'Imagen' : 'PDF'}
          </button>
        ))}
      </div>

      {kind === 'text' && (
        <Field label="Texto">
          <textarea className="input min-h-20" value={text} onChange={(e) => setText(e.target.value)} />
        </Field>
      )}

      {kind !== 'text' && (
        <div className="space-y-2">
          <Field label={kind === 'voice' ? 'Archivo de audio (mp3/m4a/ogg/wav — se envía como nota de voz)'
            : kind === 'image' ? 'Imagen' : 'PDF'}>
            <input ref={fileInputRef} type="file" accept={accept}
              onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
            {file && <div className="text-xs text-slate-500 mt-1">
              {file.name} — {(file.size / 1024).toFixed(0)} KB · {file.type || 'tipo desconocido'}
            </div>}
            {kind === 'voice' && (
              <div className="text-xs text-slate-400 mt-1">
                Evolution convierte el audio a OGG/Opus y lo envía como PTT (nota de voz)
                en el chat de WhatsApp.
              </div>
            )}
          </Field>
          {kind !== 'voice' && (
            <Field label="Caption (opcional)">
              <input className="input" value={caption} onChange={(e) => setCaption(e.target.value)} />
            </Field>
          )}
        </div>
      )}

      <button className="btn-primary" onClick={send} disabled={sending}>
        {sending ? 'Enviando...' : 'Enviar prueba'}
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
      update(stepIndex, { mediaAssetId: data.id });
      toast.success(`Subido: ${data.fileName}`);
    } catch (err: any) {
      toast.error(err?.response?.data?.error ?? 'Falló la subida');
    }
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
        const asset = s.mediaAssetId ? mediaById[s.mediaAssetId] : null;
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
              placeholder={asset
                ? (asset.mimeType.startsWith('audio/')
                    ? 'Texto opcional (sale como mensaje separado antes del audio)'
                    : 'Caption opcional (texto que va con el archivo)')
                : (i === 0 ? 'ej. {Hola!|Buenas!} {name}, ...' : 'ej. te dejo el link: {checkout_url}')}
              value={s.text}
              onChange={(e) => update(i, { text: e.target.value })}
            />
            <AttachmentSlot
              asset={asset}
              onPick={(file) => handleUpload(i, file)}
              onRemove={() => update(i, { mediaAssetId: null })}
            />
          </div>
        );
      })}
    </div>
  );
}

function AttachmentSlot({ asset, onPick, onRemove }: {
  asset: MediaAsset | null | undefined;
  onPick: (file: File) => void;
  onRemove: () => void;
}) {
  const inputId = `att-${Math.random().toString(36).slice(2, 8)}`;
  const isImage = asset?.mimeType.startsWith('image/');
  const isAudio = asset?.mimeType.startsWith('audio/');
  // Preview va directo al backend (no pasa por el axios client), así que armamos
  // la URL completa con la base usada por el resto de la app.
  const apiBase = (import.meta.env.VITE_API_URL ?? '/api').replace(/\/$/, '');
  const previewUrl = asset ? `${apiBase}/media/${asset.id}` : null;

  if (asset) {
    return (
      <div className="space-y-1">
        <div className="flex items-center gap-2 text-xs bg-white border border-slate-200 rounded p-2">
          {isImage && previewUrl ? (
            <img src={previewUrl} alt={asset.fileName} className="w-12 h-12 object-cover rounded border border-slate-200" />
          ) : (
            <div className="w-12 h-12 rounded border border-slate-200 grid place-items-center bg-slate-50 text-slate-400">
              {isAudio ? '🎤' : '📄'}
            </div>
          )}
          <div className="flex-1 min-w-0">
            <div className="font-medium truncate">{asset.fileName}</div>
            <div className="text-slate-500 text-[11px]">
              {asset.mimeType} · {(asset.sizeBytes / 1024).toFixed(0)} KB
              {isAudio && <span className="ml-1 text-emerald-700">· se envía como nota de voz</span>}
            </div>
            {isAudio && previewUrl && (
              <audio controls preload="none" className="w-full mt-1 h-8">
                <source src={previewUrl} type={asset.mimeType} />
              </audio>
            )}
          </div>
          <button type="button" className="btn-secondary text-xs text-rose-600" onClick={onRemove}>Quitar</button>
        </div>
        {isAudio && (
          <div className="text-[11px] text-slate-400 pl-1">
            WhatsApp no permite caption en notas de voz: si dejás texto en el paso, se manda
            como mensaje separado ANTES del audio.
          </div>
        )}
      </div>
    );
  }

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
          e.target.value = ''; // reset para permitir mismo file dos veces
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
