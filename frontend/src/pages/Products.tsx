import { useState, useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import type { CategoryCadence, MediaAsset, MessageStep, Product, Seller } from '../lib/types';
import Switch from '../components/Switch';

const EMPTY: Product = {
  id: '', productKey: '', displayName: '', active: true, country: 'AR', countryName: 'Argentina',
  regionCode: 'ar', language: 'es', phonePrefix: '54', categories: [], messageTemplate: '',
  openerTemplate: '',
  checkoutUrl: '', priceDisplay: '', dailyLimit: 60, triggerHours: [10, 14, 18],
  sendHourStart: 10, sendHourEnd: 20,
  requiresAssistedSale: false,
  googlePlacesDailyLeadCap: 60,
  replyTemplates: [],
  messageSteps: [],
  categoryCadences: [],
  metaAdsMessageSteps: [],
  aiSalesPlaybook: '',
  autoPilot: true,
  autoReengage: true
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

  // Toggle directo de piloto/re-enganche desde la lista (endpoint chico, no pisa el resto).
  async function toggleAutomation(p: Product, patch: { autoPilot?: boolean; autoReengage?: boolean }) {
    const autoPilot = patch.autoPilot ?? p.autoPilot;
    const autoReengage = patch.autoReengage ?? p.autoReengage;
    try {
      await api.post(`/products/${p.id}/automation`, { autoPilot, autoReengage });
      toast.success(autoPilot ? 'Piloto actualizado' : 'Piloto apagado');
      qc.invalidateQueries({ queryKey: ['products-admin'] });
      if (selected?.id === p.id) setDraft((d) => ({ ...d, autoPilot, autoReengage: autoPilot && autoReengage }));
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
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
            <div key={p.id}
              className={`p-3 flex items-center gap-3 hover:bg-slate-50 ${selected?.id === p.id ? 'bg-brand-50' : ''}`}>
              <button onClick={() => setSelected(p)} className="flex-1 text-left min-w-0">
                <div className="font-medium truncate">{p.displayName}</div>
                <div className="text-xs text-slate-500 truncate">{p.productKey} — {p.active ? 'activo' : 'pausado'}</div>
              </button>
              <WhatsappLineBadge productKey={p.productKey} />
              <div className="flex items-center gap-3 shrink-0">
                <div className="flex flex-col items-center gap-0.5">
                  <Switch on={p.autoPilot} onClick={() => toggleAutomation(p, { autoPilot: !p.autoPilot })}
                    title="Piloto automático: el bot responde y vende solo" />
                  <span className="text-[10px] text-slate-400">piloto</span>
                </div>
                <div className="flex flex-col items-center gap-0.5">
                  <Switch on={p.autoReengage} disabled={!p.autoPilot}
                    onClick={() => toggleAutomation(p, { autoReengage: !p.autoReengage })}
                    title={p.autoPilot ? 'Re-enganche proactivo a leads dormidos' : 'Necesita Piloto ON'} />
                  <span className="text-[10px] text-slate-400">re-enganche</span>
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>

      <div className="md:col-span-8 card p-4 md:p-5 space-y-3">
        <h3 className="font-semibold">{selected?.id ? `Editar: ${selected.displayName}` : 'Nuevo producto'}</h3>

        {/* Lo esencial siempre visible: identificador + nombre + categorías */}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label="product_key">
            <input className="input" value={draft.productKey} onChange={(e) => onChange('productKey', e.target.value)} disabled={!!selected?.id} />
          </Field>
          <Field label="Nombre"><input className="input" value={draft.displayName} onChange={(e) => onChange('displayName', e.target.value)} /></Field>
        </div>
        <Field label="Categorías de búsqueda Google Maps (coma)">
          <CategoriesInput value={draft.categories} onChange={(v) => onChange('categories', v)} />
        </Field>

        {/* Resto colapsado — casi nunca se toca después de crear */}
        <details className="border border-slate-200 rounded p-2">
          <summary className="cursor-pointer text-sm text-slate-600 hover:text-slate-900 font-medium">
            Configuración avanzada
            <span className="ml-2 text-xs text-slate-400 font-normal">
              país · idioma · checkout · caps · ventana horaria
            </span>
          </summary>
          <div className="mt-3 space-y-3">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
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
            <Field label="Reglas / Playbook de la IA — cómo querés que responda el bot (lenguaje natural)">
              <textarea className="input font-mono text-xs" rows={6}
                value={draft.aiSalesPlaybook}
                onChange={(e) => onChange('aiSalesPlaybook', e.target.value)}
                placeholder={'Ej:\n- Tuteá siempre, tono relajado y canchero, máx 2 líneas por mensaje.\n- Si preguntan el precio, dalo y proponé una demo el mismo día.\n- Si ya usan otro software, preguntá cuál y ofrecé migración gratis.\n- No insistas más de 2 veces si no contestan.'} />
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
          </div>
        </details>
        <CadencesEditor
          productKey={selected?.productKey ?? draft.productKey}
          categories={draft.categories}
          defaultSteps={draft.messageSteps ?? []}
          overrides={draft.categoryCadences ?? []}
          metaSteps={draft.metaAdsMessageSteps ?? []}
          onChangeDefault={(steps) => onChange('messageSteps', steps)}
          onChangeOverrides={(over) => onChange('categoryCadences', over)}
          onChangeMeta={(steps) => onChange('metaAdsMessageSteps', steps)}
        />
        <Field label="Respuestas rápidas (una por línea)">
          <ReplyTemplatesEditor
            value={draft.replyTemplates ?? []}
            onChange={(v) => onChange('replyTemplates', v)}
          />
          <div className="text-xs text-slate-400 mt-1 space-y-0.5">
            <div>Aparecen como botones arriba del input en la pantalla de chat.</div>
            <div>
              Sintaxis comando: <code>/clave = contenido</code> — al tipear <code>/clave</code> seguido de espacio
              en el input, se reemplaza por el contenido.
            </div>
            <div>
              Salto de línea en el contenido: usá <code>\n</code> literal (ej.{' '}
              <code>/precio = línea1\nlínea2</code>).
            </div>
          </div>
        </Field>
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.active} onChange={(e) => onChange('active', e.target.checked)} />
            Activo
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.requiresAssistedSale} onChange={(e) => onChange('requiresAssistedSale', e.target.checked)} />
            Requiere venta asistida (demo con admin)
          </label>
          <label className="flex items-center gap-2 text-sm font-medium text-indigo-700"
            title="Si está prendido, el bot RESPONDE SOLO los mensajes entrantes por WhatsApp (con ritmo humano y tope diario por número). Bajo riesgo: es como una persona contestando.">
            <input type="checkbox" checked={draft.autoPilot} onChange={(e) => onChange('autoPilot', e.target.checked)} />
            🤖 Piloto automático (responde inbound solo)
          </label>
          <label className="flex items-center gap-2 text-sm font-medium text-rose-700"
            title="Además de responder, manda los re-enganches PROACTIVOS solo (a los que se quedaron callados), capeado al límite diario del vendedor. Más alcance, más riesgo de ban. Requiere Piloto automático prendido.">
            <input type="checkbox" checked={draft.autoReengage} onChange={(e) => onChange('autoReengage', e.target.checked)} />
            📣 Re-enganche proactivo automático
          </label>
          <button className="btn-primary ml-auto" onClick={save}>Guardar</button>
        </div>

        <TestSendPanel
          productId={selected?.id ?? ''}
          defaultPrefix={draft.phonePrefix}
          categories={draft.categories}
          overrideCats={(draft.categoryCadences ?? []).map((c) => c.category)}
          hasSteps={(draft.messageSteps ?? []).length > 0 || (draft.categoryCadences ?? []).some((c) => c.steps.length > 0)}
        />
      </div>
    </div>
  );
}

function TestSendPanel({ productId, defaultPrefix, hasSteps, categories, overrideCats }: {
  productId: string;
  defaultPrefix: string;
  hasSteps: boolean;
  categories: string[];
  overrideCats: string[];
}) {
  const sellersQ = useQuery({
    queryKey: ['sellers-admin'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });
  const connected = (sellersQ.data ?? []).filter((s) => s.instanceStatus === 'Connected');

  const [sellerId, setSellerId] = useState('');
  const [phone, setPhone] = useState('');
  const [category, setCategory] = useState('');
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
        { productId, sellerId, phone: cleaned, category: category || null }
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
        {categories.length > 0 && (
          <Field label="Categoría a probar">
            <select className="input" value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="">Default (sin categoría)</option>
              {categories.map((c) => {
                const hasOverride = overrideCats.includes(c);
                return (
                  <option key={c} value={c}>
                    {c} {hasOverride ? '· override propio' : '· cae al default'}
                  </option>
                );
              })}
            </select>
          </Field>
        )}
      </div>

      <div className="text-xs text-slate-500">
        Se envían los pasos en orden respetando el <b>delay configurado de cada paso</b>
        (capeado a 10 min). Si un paso tiene varias variantes de audio, se manda la
        <b>primera</b> para que la prueba sea determinística. La categoría seleccionada
        decide qué cadencia se dispara (override propio o default).
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

/// Input de lista separada por comas. Como ReplyTemplatesEditor: mantiene el
/// texto crudo mientras el seller tipea (de otra forma split+trim+join se
/// come la coma final apenas la apretás y no podés agregar más items).
function CategoriesInput({ value, onChange }: {
  value: string[];
  onChange: (v: string[]) => void;
}) {
  const [raw, setRaw] = useState(() => value.join(', '));
  useEffect(() => {
    const canonical = value.join(', ');
    setRaw((prev) => {
      // Si lo que el seller está tipeando, una vez normalizado, equivale al
      // canónico, no pisamos el draft (preserva commas trailing y espacios).
      const norm = prev.split(',').map((s) => s.trim()).filter(Boolean).join(', ');
      return norm === canonical ? prev : canonical;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value.join(',')]);

  function commit(text: string) {
    onChange(text.split(',').map((s) => s.trim()).filter(Boolean));
  }

  return (
    <input className="input" value={raw}
      onChange={(e) => setRaw(e.target.value)}
      onBlur={() => commit(raw)}
    />
  );
}

/// Mantiene el texto crudo del textarea mientras el seller tipea (no podés
/// hacer .trim() en cada keystroke porque te bloquea el espacio al final).
/// Solo limpiamos las líneas al perder el focus o al cambiar de paso.
function ReplyTemplatesEditor({ value, onChange }: {
  value: string[];
  onChange: (v: string[]) => void;
}) {
  const [raw, setRaw] = useState<string>(() => value.join('\n'));
  // Si el seller selecciona otro producto, refrescamos el textarea con el
  // nuevo valor canónico. Comparamos por contenido para no pisar lo que el
  // seller está editando ahora.
  useEffect(() => {
    const canonical = value.join('\n');
    setRaw((prev) => {
      // Si lo único que difiere es trailing whitespace, dejamos el draft.
      if (prev.split('\n').map((s) => s.trim()).filter(Boolean).join('\n') === canonical) {
        return prev;
      }
      return canonical;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value.join('\n')]);

  function commit(text: string) {
    const lines = text.split('\n').map((s) => s.trim()).filter(Boolean);
    onChange(lines);
  }

  return (
    <textarea
      className="input min-h-24 text-sm font-mono"
      placeholder={'¿Te interesa que te pase más info?\n/precio = Mensual: $5000\\nAnual: $50000\\nDemo: gratis 30 días\n/checkout = Te paso el link: {checkout_url}'}
      value={raw}
      onChange={(e) => setRaw(e.target.value)}
      onBlur={() => commit(raw)}
    />
  );
}

/// Deep copy de un array de MessageStep para que los overrides empiecen como
/// copia independiente del default — modificar uno no toca el otro.
function cloneSteps(src: MessageStep[]): MessageStep[] {
  return (src ?? []).map((s) => ({
    text: s.text,
    delaySeconds: s.delaySeconds,
    mediaAssetId: s.mediaAssetId ?? null,
    mediaAssetIds: [...(s.mediaAssetIds ?? [])]
  }));
}

// Sentinel para la tab de cadencia Meta Ads (no puede colisionar con una
// categoría real: las categorías vienen de búsquedas de Maps, sin "::").
const META_TAB = '::meta-ads';

function CadencesEditor({
  productKey, categories, defaultSteps, overrides, metaSteps, onChangeDefault, onChangeOverrides, onChangeMeta
}: {
  productKey: string;
  categories: string[];
  defaultSteps: MessageStep[];
  overrides: CategoryCadence[];
  metaSteps: MessageStep[];
  onChangeDefault: (s: MessageStep[]) => void;
  onChangeOverrides: (o: CategoryCadence[]) => void;
  onChangeMeta: (s: MessageStep[]) => void;
}) {
  // Tab activa: '' = default; META_TAB = cadencia Meta Ads; otro string =
  // nombre de la categoría override.
  const [activeTab, setActiveTab] = useState<string>('');
  const overrideMap = new Map(overrides.map((o) => [o.category, o.steps]));
  const overriddenCats = new Set(overrides.map((o) => o.category));
  const availableCategoriesToAdd = categories.filter((c) => !overriddenCats.has(c));

  function addOverride(cat: string) {
    if (!cat || overriddenCats.has(cat)) return;
    // Pre-cargamos con una copia profunda del default. El seller arranca
    // editando lo que ya tenía y solo cambia lo específico (típicamente el
    // texto del primer paso o el audio).
    onChangeOverrides([...overrides, { category: cat, steps: cloneSteps(defaultSteps) }]);
    setActiveTab(cat);
  }
  function removeOverride(cat: string) {
    if (!confirm(`Quitar el override de "${cat}"? Los leads de esa categoría van a volver a usar el default.`)) return;
    onChangeOverrides(overrides.filter((o) => o.category !== cat));
    if (activeTab === cat) setActiveTab('');
  }
  function updateOverrideSteps(cat: string, steps: MessageStep[]) {
    onChangeOverrides(overrides.map((o) => o.category === cat ? { ...o, steps } : o));
  }
  function importDefaultIntoActive() {
    if (activeTab === '') return;
    if (!confirm(`Reemplazar los pasos de "${activeTab === META_TAB ? 'Meta Ads' : activeTab}" con los del default? Lo editado se pierde.`)) return;
    if (activeTab === META_TAB) onChangeMeta(cloneSteps(defaultSteps));
    else updateOverrideSteps(activeTab, cloneSteps(defaultSteps));
  }
  function clearMetaSteps() {
    if (!confirm('Vaciar la cadencia Meta Ads? Esos leads van a volver a recibir la cadencia default.')) return;
    onChangeMeta([]);
    setActiveTab('');
  }

  const stepsForActiveTab = activeTab === ''
    ? defaultSteps
    : activeTab === META_TAB
      ? metaSteps
      : (overrideMap.get(activeTab) ?? []);
  const onChangeForActiveTab = activeTab === ''
    ? onChangeDefault
    : activeTab === META_TAB
      ? onChangeMeta
      : (s: MessageStep[]) => updateOverrideSteps(activeTab, s);

  const noOverrideCats = categories.filter((c) => !overriddenCats.has(c));

  return (
    <div className="space-y-3">
      <div className="border-b border-slate-200">
        <div className="flex items-center gap-1 flex-wrap">
          <button type="button"
            className={`text-xs px-3 py-1.5 -mb-px border-b-2 ${activeTab === '' ? 'border-brand-600 text-brand-700 font-semibold' : 'border-transparent text-slate-500 hover:text-slate-800'}`}
            onClick={() => setActiveTab('')}>
            Default
            <span className="ml-1 text-[10px] text-slate-400">({defaultSteps.length})</span>
          </button>
          <button type="button"
            className={`text-xs px-3 py-1.5 -mb-px border-b-2 ${activeTab === META_TAB ? 'border-indigo-600 text-indigo-700 font-semibold' : 'border-transparent text-slate-500 hover:text-slate-800'}`}
            title="Cadencia para leads que llegan del formulario de Meta Lead Ads (paso a paso de alta de cuenta)"
            onClick={() => setActiveTab(META_TAB)}>
            📣 Meta Ads
            <span className={`ml-1 text-[10px] ${metaSteps.length > 0 ? 'text-indigo-700' : 'text-slate-400'}`}>({metaSteps.length})</span>
          </button>
          {overrides.map((o) => (
            <button key={o.category} type="button"
              className={`text-xs px-3 py-1.5 -mb-px border-b-2 ${activeTab === o.category ? 'border-brand-600 text-brand-700 font-semibold' : 'border-transparent text-slate-500 hover:text-slate-800'}`}
              onClick={() => setActiveTab(o.category)}>
              {o.category}
              <span className="ml-1 text-[10px] text-emerald-700">({o.steps.length})</span>
            </button>
          ))}
          {availableCategoriesToAdd.length > 0 && (
            <select
              className="ml-auto text-xs px-2 py-1 border border-slate-200 rounded bg-white text-slate-600"
              value=""
              onChange={(e) => { if (e.target.value) addOverride(e.target.value); e.target.selectedIndex = 0; }}>
              <option value="">+ Override de categoría</option>
              {availableCategoriesToAdd.map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          )}
        </div>
      </div>

      {activeTab === META_TAB && (
        <div className="flex items-center justify-between bg-indigo-50 border border-indigo-200 rounded p-2 text-xs gap-2 flex-wrap">
          <span>
            Cadencia <b>Meta Ads</b> — para leads que dejaron sus datos en el formulario del anuncio:
            acá va el <b>paso a paso de alta de cuenta</b>, no el opener frío. Salen con prioridad
            (antes que cualquier cold outreach). Vacía = usan el default.
          </span>
          <div className="flex gap-3">
            <button type="button"
              className="text-indigo-700 hover:underline"
              title="Reemplaza los pasos actuales con una copia de los del default"
              onClick={importDefaultIntoActive}>
              ⤓ Importar del default
            </button>
            <button type="button"
              className="text-rose-600 hover:underline"
              onClick={clearMetaSteps}>
              Vaciar
            </button>
          </div>
        </div>
      )}

      {activeTab !== '' && activeTab !== META_TAB && (
        <div className="flex items-center justify-between bg-emerald-50 border border-emerald-200 rounded p-2 text-xs gap-2 flex-wrap">
          <span>
            Cadencia <b>{activeTab}</b> — solo se usa cuando el lead viene de esa búsqueda.
          </span>
          <div className="flex gap-3">
            <button type="button"
              className="text-emerald-700 hover:underline"
              title="Reemplaza los pasos actuales con una copia de los del default"
              onClick={importDefaultIntoActive}>
              ⤓ Importar del default
            </button>
            <button type="button"
              className="text-rose-600 hover:underline"
              onClick={() => removeOverride(activeTab)}>
              Quitar override
            </button>
          </div>
        </div>
      )}

      {activeTab === '' && noOverrideCats.length > 0 && (
        <div className="text-[11px] text-slate-500">
          Categorías sin override (caen al default): {noOverrideCats.join(', ')}
        </div>
      )}

      <StepsEditor
        productKey={productKey}
        steps={stepsForActiveTab}
        onChange={onChangeForActiveTab}
      />
    </div>
  );
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
  /// Saca el texto del paso de audio y lo mete en un paso NUEVO antes,
  /// con delay 0. El paso de audio queda con su delay original — así el
  /// seller controla cuánto tiempo pasa entre el texto y el audio.
  function splitTextFromAudio(stepIndex: number) {
    const cur = steps[stepIndex];
    const audioOnly: MessageStep = { ...cur, text: '' };
    const textStep: MessageStep = {
      text: cur.text,
      delaySeconds: stepIndex === 0 ? 0 : cur.delaySeconds,
      mediaAssetId: null,
      mediaAssetIds: []
    };
    // El paso de audio hereda el delay relativo al texto que acabamos de
    // crear. Si era el primer paso, el texto queda en posición 0 (delay 0)
    // y el audio en 1 con un delay default razonable.
    if (stepIndex === 0) audioOnly.delaySeconds = 60;
    else audioOnly.delaySeconds = 60;
    const next = [...steps];
    next.splice(stepIndex, 1, textStep, audioOnly);
    onChange(next);
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

      {steps.map((s, i) => (
        <StepRow
          key={i}
          step={s}
          index={i}
          totalSteps={steps.length}
          mediaById={mediaById}
          onUpdate={(patch) => update(i, patch)}
          onMove={(dir) => move(i, dir)}
          onRemove={() => remove(i)}
          onUpload={(file) => handleUpload(i, file)}
          onRemoveVariant={(assetId) => removeVariant(i, assetId)}
          onClearMedia={() => clearStepMedia(i)}
        />
      ))}
    </div>
  );
}

type StepKind = 'text' | 'audio' | 'image' | 'document';
const KIND_LABEL: Record<StepKind, string> = {
  text: '💬 Texto',
  audio: '🎤 Audio',
  image: '🖼️ Imagen',
  document: '📄 PDF'
};

function StepRow({
  step: s, index: i, totalSteps, mediaById,
  onUpdate, onMove, onRemove, onUpload, onRemoveVariant, onClearMedia
}: {
  step: MessageStep;
  index: number;
  totalSteps: number;
  mediaById: Record<string, MediaAsset>;
  onUpdate: (patch: Partial<MessageStep>) => void;
  onMove: (dir: -1 | 1) => void;
  onRemove: () => void;
  onUpload: (file: File) => void;
  onRemoveVariant: (assetId: string) => void;
  onClearMedia: () => void;
}) {
  const variantIds = (s.mediaAssetIds && s.mediaAssetIds.length > 0)
    ? s.mediaAssetIds
    : (s.mediaAssetId ? [s.mediaAssetId] : []);
  const variantAssets = variantIds.map(id => mediaById[id]).filter(Boolean);
  const firstAsset = variantAssets[0];

  const detectedFromAsset: StepKind = !firstAsset ? 'text'
    : firstAsset.mimeType.startsWith('audio/') ? 'audio'
    : firstAsset.mimeType.startsWith('image/') ? 'image'
    : 'document';

  // Si el seller eligió un tipo (ej. Audio) pero todavía no subió asset, lo
  // recordamos en state local. El render sigue ese intent. Cuando sube algo
  // o si vuelve a Texto, lo reseteamos.
  const [intentKind, setIntentKind] = useState<StepKind | null>(null);
  const kind: StepKind = intentKind ?? detectedFromAsset;
  // Si el asset detected coincide con intentKind, ya no hace falta el override.
  useEffect(() => {
    if (intentKind && intentKind === detectedFromAsset) setIntentKind(null);
  }, [intentKind, detectedFromAsset]);

  function setKind(k: StepKind) {
    if (k === kind) return;
    if (k === 'text') {
      if (firstAsset && !confirm('Pasar este paso a Texto va a quitar el adjunto. ¿Seguir?')) return;
      onUpdate({ mediaAssetId: null, mediaAssetIds: [] });
      setIntentKind(null);
      return;
    }
    if (firstAsset && firstAsset.mimeType.startsWith(
      k === 'audio' ? 'audio/' : k === 'image' ? 'image/' : 'application/'
    )) {
      // El asset existente sirve para el nuevo tipo: no tocar nada.
      setIntentKind(null);
      return;
    }
    if (firstAsset && !confirm('Cambiar el tipo de paso va a quitar el adjunto actual. ¿Seguir?')) return;
    onUpdate({ mediaAssetId: null, mediaAssetIds: [] });
    setIntentKind(k);
  }

  return (
    <div className="border border-slate-200 rounded-md p-3 bg-slate-50/40 space-y-2">
      <div className="flex items-center gap-2 text-xs flex-wrap">
        <span className="font-semibold text-slate-700">Paso {i + 1}</span>
        {i === 0 ? (
          <span className="text-slate-400">(sale al asignar — delay siempre 0)</span>
        ) : (
          <DelayInput
            seconds={s.delaySeconds}
            onChange={(sec) => onUpdate({ delaySeconds: sec })}
          />
        )}
        <div className="ml-auto flex gap-1">
          <button type="button" className="btn-secondary text-xs px-2 py-0.5"
            disabled={i === 0} onClick={() => onMove(-1)} title="Subir">↑</button>
          <button type="button" className="btn-secondary text-xs px-2 py-0.5"
            disabled={i === totalSteps - 1} onClick={() => onMove(1)} title="Bajar">↓</button>
          <button type="button" className="btn-secondary text-xs px-2 py-0.5 text-rose-600"
            onClick={onRemove} title="Eliminar">×</button>
        </div>
      </div>

      <div className="flex gap-1 flex-wrap">
        {(['text', 'audio', 'image', 'document'] as StepKind[]).map((k) => (
          <button key={k} type="button"
            className={`text-xs px-2 py-1 rounded border ${kind === k
              ? 'bg-brand-600 text-white border-brand-600'
              : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50'}`}
            onClick={() => setKind(k)}>
            {KIND_LABEL[k]}
          </button>
        ))}
      </div>

      {kind === 'text' && (
        <textarea
          className="input min-h-20 font-mono text-sm w-full"
          placeholder={i === 0 ? 'ej. {Hola!|Buenas!} {name}, ...' : 'ej. te dejo el link: {checkout_url}'}
          value={s.text}
          onChange={(e) => onUpdate({ text: e.target.value })}
        />
      )}

      {kind === 'audio' && (
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
              onRemove={() => onRemoveVariant(a.id)}
            />
          ))}
          <AddVariantButton onPick={onUpload} />
        </div>
      )}

      {(kind === 'image' || kind === 'document') && (
        <div className="space-y-2">
          {firstAsset ? (
            <AttachmentRow asset={firstAsset} onRemove={onClearMedia} />
          ) : (
            <EmptyAttachment
              accept={kind === 'image' ? 'image/*' : 'application/pdf,application/*'}
              label={kind === 'image' ? 'Subir imagen' : 'Subir PDF'}
              onPick={onUpload}
            />
          )}
          {firstAsset && (
            <textarea
              className="input min-h-12 text-sm w-full"
              placeholder="Caption opcional (texto que va con el archivo)"
              value={s.text}
              onChange={(e) => onUpdate({ text: e.target.value })}
            />
          )}
        </div>
      )}
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

function EmptyAttachment({ onPick, accept, label }: {
  onPick: (file: File) => void;
  accept?: string;
  label?: string;
}) {
  const inputId = `att-${Math.random().toString(36).slice(2, 8)}`;
  return (
    <label htmlFor={inputId}
      className="flex items-center gap-2 text-xs border border-dashed border-slate-300 rounded p-2 cursor-pointer hover:bg-slate-50">
      <span className="text-lg">📎</span>
      <span className="flex-1 text-slate-500">{label ?? 'Adjuntar archivo'}</span>
      <input id={inputId} type="file" className="hidden"
        accept={accept ?? 'audio/*,image/*,application/pdf'}
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

// ── Línea WhatsApp propia por app: estado + conexión por QR ─────────────────
type AppLine = { productKey: string; instanceName: string; connectedPhoneNumber?: string | null; status: string };

function WhatsappLineBadge({ productKey }: { productKey: string }) {
  const qc = useQueryClient();
  const [showQr, setShowQr] = useState(false);
  const linesQ = useQuery({
    queryKey: ['app-wa-lines'],
    queryFn: async () => (await api.get<AppLine[]>('/products/whatsapp-lines')).data,
    refetchInterval: 20000,
  });
  const line = (linesQ.data ?? []).find((l) => l.productKey === productKey);
  const connected = line?.status === 'Connected';

  const qrQ = useQuery({
    queryKey: ['app-wa-qr', productKey],
    enabled: showQr,
    refetchInterval: showQr ? 15000 : false, // el QR de WhatsApp expira ~20s
    queryFn: async () => (await api.get<{ qrBase64?: string; status: string }>(`/products/${productKey}/whatsapp/qr`)).data,
  });

  useEffect(() => {
    if (showQr && (qrQ.data?.status === 'open' || qrQ.data?.status === 'connected')) {
      toast.success(`WhatsApp de ${productKey} conectado ✅`);
      setShowQr(false);
      qc.invalidateQueries({ queryKey: ['app-wa-lines'] });
    }
  }, [qrQ.data?.status, showQr]);

  return (
    <>
      <button
        className={`text-[10px] px-1.5 py-0.5 rounded border shrink-0 ${connected
          ? 'border-emerald-300 bg-emerald-50 text-emerald-700'
          : 'border-slate-300 bg-slate-50 text-slate-500 hover:bg-slate-100'}`}
        title={connected
          ? `Línea propia conectada: ${line?.connectedPhoneNumber ?? ''} (${line?.instanceName})`
          : 'Conectar un número de WhatsApp propio para esta app (escaneás un QR)'}
        onClick={(e) => { e.stopPropagation(); setShowQr(true); }}>
        {connected ? `📱 ${line?.connectedPhoneNumber ?? 'conectado'}` : '📱 conectar'}
      </button>

      {showQr && (
        <div className="fixed inset-0 z-50 bg-black/50 flex items-center justify-center p-4" onClick={() => setShowQr(false)}>
          <div className="bg-white rounded-xl p-5 max-w-sm w-full text-center space-y-3" onClick={(e) => e.stopPropagation()}>
            <h3 className="font-semibold">WhatsApp de {productKey}</h3>
            <p className="text-xs text-slate-500">
              En el celular con el número para esta app: WhatsApp → Dispositivos vinculados → Vincular dispositivo → escaneá.
            </p>
            {qrQ.isLoading ? (
              <div className="py-10 text-slate-400 text-sm">Generando QR…</div>
            ) : qrQ.data?.qrBase64 ? (
              <img src={qrQ.data.qrBase64.startsWith('data:') ? qrQ.data.qrBase64 : `data:image/png;base64,${qrQ.data.qrBase64}`}
                alt="QR" className="mx-auto w-56 h-56" />
            ) : (
              <div className="py-10 text-slate-400 text-sm">
                {qrQ.data?.status === 'open' ? 'Ya está conectada ✅' : 'Sin QR (probá de nuevo en unos segundos)'}
              </div>
            )}
            <div className="text-[11px] text-slate-400">Estado: {qrQ.data?.status ?? '…'} — el QR se renueva solo cada 15s</div>
            <button className="btn-secondary text-sm w-full" onClick={() => setShowQr(false)}>Cerrar</button>
          </div>
        </div>
      )}
    </>
  );
}
