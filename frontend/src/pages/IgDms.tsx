import { useEffect, useState, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Sender = 'extension' | 'device';

type Settings = {
  productKey: string; displayName: string; enabled: boolean;
  accountUsername?: string | null; defaultAccount?: string | null;
  dailyCap: number; minGapMinutes: number; maxGapMinutes: number; fromHour: number; toHour: number;
  template: string; pausedUntil?: string | null; pausedReason?: string | null;
  sentToday: number; pending: number; repliesToConversations: boolean;
  // Plan: quién manda y la rampa por semana.
  sender: Sender; deviceId?: string | null;
  coldRamp?: string | null; rampStartedOn?: string | null; week: number; capToday: number; throttledUntil?: string | null;
  // Seguidores (solo con celular).
  followersEnabled: boolean; followerRamp?: string | null; followerTemplate: string; followerExclude?: string | null;
  followerCapToday: number; followerSentToday: number; followersPending: number; followersTotal: number;
  followerScanAt?: string | null;
  // Qué le contestó SalesHub al celular la última vez que preguntó por Instagram.
  deviceAskedAt?: string | null; deviceStatus?: string | null; deviceAppVersion?: string | null;
};

/** Lo que se edita en la tarjeta. El resto de Settings son números del día que cambian solos. */
type Draft = {
  enabled: boolean; accountUsername: string; dailyCap: number; minGapMinutes: number; maxGapMinutes: number;
  fromHour: number; toHour: number; template: string;
  sender: Sender; deviceId: string; coldRamp: string;
  followersEnabled: boolean; followerRamp: string; followerTemplate: string; followerExclude: string;
};

type Device = { id: string; name: string; status: string; appVersion?: string | null };

type DeviceTask = {
  id: string; kind: string; status: string; handle?: string | null; text?: string | null; error?: string | null;
  screen?: string | null; collected?: number | null; createdAt: string; finishedAt?: string | null;
};

const draftOf = (s: Settings): Draft => ({
  enabled: s.enabled, accountUsername: s.accountUsername ?? '', dailyCap: s.dailyCap,
  minGapMinutes: s.minGapMinutes, maxGapMinutes: s.maxGapMinutes, fromHour: s.fromHour, toHour: s.toHour,
  template: s.template, sender: s.sender, deviceId: s.deviceId ?? '', coldRamp: s.coldRamp ?? '',
  followersEnabled: s.followersEnabled, followerRamp: s.followerRamp ?? '', followerTemplate: s.followerTemplate,
  followerExclude: s.followerExclude ?? '',
});

const PRESETS = [
  { key: 'cauteloso', label: 'Cauteloso', hint: 'Para arrancar o después de un aviso de Instagram.', dailyCap: 10, minGapMinutes: 6, maxGapMinutes: 15 },
  { key: 'equilibrado', label: 'Equilibrado', hint: 'Cuando la cuenta ya mandó una semana sin avisos.', dailyCap: 20, minGapMinutes: 4, maxGapMinutes: 10 },
];

/** DMs en frío por día en la semana 1, 2, 3 y de la 4 en adelante. */
const COLD_RAMPS = [
  { value: '10,20,30,40', hint: 'Cuenta grande (GymHero)' },
  { value: '10,15,20,30', hint: 'Cuenta mediana (TurnosPro)' },
  { value: '5,10,15,20', hint: 'Cuenta chica (PlayCrew, EFCloud)' },
  { value: '5,10,10,15', hint: 'DashFlow' },
  { value: '5,5,10,10', hint: 'Bunker' },
];
const FOLLOWER_RAMPS = [{ value: '20,40,50,60', hint: 'GymHero' }];

const TASK_KIND: Record<string, string> = {
  verify: 'Confirmar la cuenta', followers: 'Leer seguidores', dm_cold: 'DM en frío', dm_follower: 'DM a seguidor', test: 'Prueba',
};

/** Códigos que reporta el celular, en criollo. Lo que no está acá se muestra como viene. */
const TASK_ERROR: Record<string, string> = {
  blocked: 'Instagram mostró un aviso de bloqueo',
  humano_en_el_celu: 'alguien estaba usando el teléfono (se reintenta)',
  send_not_confirmed: 'no se pudo confirmar el envío (no se reintenta)',
  sin_reporte: 'el celular no avisó cómo terminó (no se reintenta)',
  perfil_no_existe: 'el perfil no existe',
  no_acepta_mensajes: 'no acepta mensajes',
  es_la_cuenta_propia: 'es la cuenta propia',
  cuenta_equivocada: 'el Instagram del celular está en otra cuenta',
  sin_sesion: 'el Instagram del celular está sin sesión iniciada',
  instagram_no_abrio: 'Instagram no abrió',
  perfil_no_cargo: 'el perfil no cargó',
  chat_no_abrio: 'el chat no abrió',
  cajon_no_encontrado: 'no encontró dónde escribir',
  boton_mensaje_no_encontrado: 'no encontró el botón Mensaje',
  boton_enviar_no_encontrado: 'no encontró el botón de enviar',
  contador_de_seguidores_no_encontrado: 'no encontró el contador de seguidores',
  lista_de_seguidores_no_abrio: 'la lista de seguidores no abrió',
  no_pude_leer_la_lista: 'no pudo leer la lista de seguidores',
  timeout: 'se le acabó el tiempo',
};

const AR = 'America/Argentina/Buenos_Aires';
const fmtDateTime = (iso: string) =>
  new Date(iso).toLocaleString('es-AR', { timeZone: AR, day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
const fmtDay = (iso: string) => new Date(iso).toLocaleDateString('es-AR', { timeZone: AR, day: '2-digit', month: '2-digit' });
const rampLabel = (v: string) => v.split(',').join(' · ');

function ago(iso: string): string {
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 90) return `hace ${s} s`;
  if (s < 5400) return `hace ${Math.round(s / 60)} min`;
  return `hace ${Math.round(s / 3600)} h`;
}

/** Versiones de un texto: bloques separados por una línea con "---". */
const variantsOf = (t: string) => t.split(/^\s*-{3,}\s*$/m).map((v) => v.trim()).filter(Boolean);

/** Lo que tipea el celular: el teclado por adb solo escribe ASCII (mismo criterio que el backend). */
const foldForDevice = (t: string) =>
  t.replace(/[“”]/g, '"').replace(/[‘’]/g, "'").replace(/[—–]/g, '-').replace(/…/g, '...')
    .normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[\n\r\t]+/g, ' ')
    .replace(/[^\x20-\x7E]/g, '').replace(/\s+/g, ' ').trim();

/** Lo que no va en un mensaje en frío (mismo criterio que WhatsApp): tildes, ¿, emojis y links. */
function copyWarnings(t: string, byDevice: boolean): string[] {
  const w: string[] = [];
  if (byDevice) {
    if (/[^\x20-\x7E\n\r\t]/.test(t)) w.push('el celular escribe sin tildes, ñ ni emojis: sale como se ve arriba');
  } else {
    if (/[áéíóúÁÉÍÓÚ]/.test(t)) w.push('tiene tildes');
    if (/[¿¡]/.test(t)) w.push('tiene ¿ o ¡');
    if (/\p{Extended_Pictographic}/u.test(t)) w.push('tiene emojis');
  }
  if (/https?:\/\/|www\.|\.com\b|\.ar\b/i.test(t)) w.push('tiene un link (en el primer DM marca como spam)');
  return w;
}

/**
 * DMs por Instagram de cada app. Los manda la extensión de Chrome o un celular Android del
 * bridge; acá se elige quién manda, el ritmo (tope fijo o rampa por semana) y, con celular, si
 * también se les escribe a los seguidores. El tope, el horario y las pausas los hace cumplir
 * el backend: el que tipea solo pregunta qué sigue.
 */
export default function IgDms() {
  const { data, isLoading } = useQuery({
    queryKey: ['ig-dm-settings'],
    queryFn: async () => (await api.get<Settings[]>('/ig-dm/settings')).data,
    refetchInterval: 30_000,
  });
  const { data: devices } = useQuery({
    queryKey: ['devices'],
    queryFn: async () => (await api.get<Device[]>('/devices')).data,
  });

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold">DMs por Instagram</h2>
        <p className="text-sm text-slate-500 max-w-3xl">
          Cada app le escribe por DM, desde su propia cuenta, a los leads que tienen @usuario de Instagram y todavía
          no fueron contactados. Puede mandar la extensión de Tampermonkey (v0.10+) desde el Chrome donde esté logueada
          esa cuenta, o un celular Android con la app SalesHub Device (1.29+) y esa cuenta abierta en Instagram. Con
          celular también se les puede escribir a los seguidores. Si Instagram muestra un aviso de bloqueo, la app se
          frena sola hasta el día siguiente y vuelve una semana a mitad de tope.
        </p>
      </div>
      {isLoading && <div className="text-sm text-slate-500">Cargando…</div>}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        {(data ?? []).map((s) => <AppCard key={s.productKey} s={s} devices={devices ?? []} />)}
      </div>
    </div>
  );
}

function AppCard({ s, devices }: { s: Settings; devices: Device[] }) {
  const qc = useQueryClient();
  const saved = draftOf(s);
  const savedKey = JSON.stringify(saved);
  const [d, setD] = useState<Draft>(saved);
  const [saving, setSaving] = useState(false);
  // Los números del día se refrescan solos; lo que se está editando solo se pisa cuando cambia lo guardado.
  useEffect(() => { setD(JSON.parse(savedKey)); }, [savedKey]);
  const dirty = JSON.stringify(d) !== savedKey;
  const set = <K extends keyof Draft>(k: K, v: Draft[K]) => setD((x) => ({ ...x, [k]: v }));

  const byDevice = d.sender === 'device';
  const savedByDevice = s.sender === 'device' && !!s.deviceId;
  const hasRamp = d.coldRamp.trim().length > 0;
  const preset = PRESETS.find((p) => p.dailyCap === d.dailyCap && p.minGapMinutes === d.minGapMinutes && p.maxGapMinutes === d.maxGapMinutes);
  const account = d.accountUsername || s.defaultAccount;

  async function save(patch: Partial<Draft> = {}, extra: { clearPause?: boolean; restartRamp?: boolean } = {}) {
    const next = { ...d, ...patch };
    setSaving(true);
    try {
      await api.put(`/ig-dm/settings/${s.productKey}`, {
        enabled: next.enabled, accountUsername: next.accountUsername || null,
        dailyCap: next.dailyCap, minGapMinutes: next.minGapMinutes, maxGapMinutes: next.maxGapMinutes,
        fromHour: next.fromHour, toHour: next.toHour, template: next.template,
        clearPause: !!extra.clearPause,
        sender: next.sender, deviceId: next.sender === 'device' ? next.deviceId || null : null,
        coldRamp: next.coldRamp, restartRamp: !!extra.restartRamp,
        followersEnabled: next.sender === 'device' && next.followersEnabled,
        followerRamp: next.followerRamp, followerTemplate: next.followerTemplate, followerExclude: next.followerExclude,
      });
      toast.success('Guardado');
      qc.invalidateQueries({ queryKey: ['ig-dm-settings'] });
    } catch (e: any) {
      // Lo que no se guardó vuelve a como estaba (si no, el interruptor queda mintiendo).
      if ('enabled' in patch) setD((x) => ({ ...x, enabled: s.enabled }));
      toast.error(e?.response?.data?.error ?? 'No se pudo guardar');
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="card p-4 space-y-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="font-semibold">{s.displayName}</div>
          <div className="text-xs text-slate-500">
            {account ? `desde @${account}` : 'sin cuenta de Instagram'}
            {s.repliesToConversations ? ' · las respuestas entran a Conversaciones' : ' · las respuestas se ven en la app de Instagram'}
          </div>
        </div>
        <Switch on={d.enabled} disabled={saving} title={d.enabled ? 'Apagar' : 'Prender'}
          onClick={() => { set('enabled', !d.enabled); save({ enabled: !d.enabled }); }} />
      </div>

      {/* Medidores del día */}
      <Meter label="Hoy en frío" sent={s.sentToday} cap={s.capToday} color="bg-pink-500"
        right={`${s.pending.toLocaleString('es-AR')} leads con Instagram en cola`} />
      {s.followersEnabled && (
        <Meter label="Hoy a seguidores" sent={s.followerSentToday} cap={s.followerCapToday} color="bg-violet-500"
          right={`${s.followersPending.toLocaleString('es-AR')} seguidores en cola de ${s.followersTotal.toLocaleString('es-AR')} leídos`} />
      )}

      {s.pausedUntil && (
        <div className="text-xs bg-amber-50 border border-amber-200 text-amber-800 rounded p-2 flex items-center gap-2">
          <span className="flex-1">
            Frenado hasta {fmtDateTime(s.pausedUntil)}
            {s.pausedReason ? `: ${s.pausedReason}` : ''}
          </span>
          <button type="button" className="underline shrink-0" disabled={saving} onClick={() => save({}, { clearPause: true })}>reanudar ya</button>
        </div>
      )}
      {s.throttledUntil && (
        <div className="text-xs bg-amber-50 border border-amber-200 text-amber-800 rounded p-2">
          A mitad de tope hasta el {fmtDay(s.throttledUntil)} por un aviso de Instagram.
        </div>
      )}

      {/* Quién manda */}
      <div className="space-y-1.5">
        <div className="text-xs text-slate-500">Quién manda</div>
        <div className="flex flex-wrap items-center gap-1.5">
          {([['extension', 'Extensión de Chrome'], ['device', 'Celular']] as [Sender, string][]).map(([key, label]) => (
            <button key={key} type="button" onClick={() => set('sender', key)}
              className={clsx('text-xs px-2.5 py-1 rounded-full border',
                d.sender === key ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50')}>
              {label}
            </button>
          ))}
          {byDevice && (
            <select className="input text-sm py-1 w-auto max-w-full" value={d.deviceId} onChange={(e) => set('deviceId', e.target.value)}>
              <option value="">Elegí el celular…</option>
              {devices.map((dev) => (
                <option key={dev.id} value={dev.id}>
                  {dev.name} · {dev.status === 'Online' ? 'conectado' : 'sin conexión'}{dev.appVersion ? ` · app ${dev.appVersion}` : ''}
                </option>
              ))}
            </select>
          )}
        </div>
        {byDevice && (
          <div className="text-xs text-slate-500">
            El celular tiene que tener Instagram abierto en {account ? `@${account}` : 'la cuenta de la app'}; lo confirma solo una vez
            por día antes de escribir. Manda Instagram cuando no tiene nada de WhatsApp para mandar.
            {savedByDevice && (
              <span className="block mt-0.5 text-slate-700">
                {s.deviceAskedAt
                  ? `Celular ${ago(s.deviceAskedAt)}${s.deviceAppVersion ? ` (app ${s.deviceAppVersion})` : ''}: ${s.deviceStatus}`
                  : 'El celular todavía no preguntó por Instagram: necesita la app 1.29 o más nueva.'}
              </span>
            )}
          </div>
        )}
      </div>

      {/* Ritmo */}
      <div className="flex flex-wrap gap-1.5">
        {PRESETS.map((p) => (
          <button key={p.key} type="button" title={p.hint}
            onClick={() => setD((x) => ({ ...x, dailyCap: p.dailyCap, minGapMinutes: p.minGapMinutes, maxGapMinutes: p.maxGapMinutes }))}
            className={clsx('text-xs px-2.5 py-1 rounded-full border',
              preset?.key === p.key ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50')}>
            {p.label}
          </button>
        ))}
        <span className={clsx('text-xs px-2.5 py-1 rounded-full border',
          !preset ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-400 border-slate-200')}>
          Personalizado
        </span>
      </div>
      <div className="grid grid-cols-2 sm:grid-cols-5 gap-2 text-xs">
        <NumField label={hasRamp ? 'Tope fijo (manda la rampa)' : 'Tope por día'} value={d.dailyCap} min={0} max={100} disabled={hasRamp} onChange={(v) => set('dailyCap', v)} />
        <NumField label="Pausa mín. (min)" value={d.minGapMinutes} min={1} max={240} onChange={(v) => set('minGapMinutes', v)} />
        <NumField label="Pausa máx. (min)" value={d.maxGapMinutes} min={1} max={240} onChange={(v) => set('maxGapMinutes', v)} />
        <NumField label="Desde (h)" value={d.fromHour} min={0} max={23} onChange={(v) => set('fromHour', v)} />
        <NumField label="Hasta (h)" value={d.toHour} min={1} max={24} onChange={(v) => set('toHour', v)} />
      </div>

      <RampField label="Rampa en frío: DMs por día en la semana 1, 2, 3…" value={d.coldRamp} options={COLD_RAMPS}
        placeholder="vacío = tope fijo" onChange={(v) => set('coldRamp', v)} />
      {s.rampStartedOn && (s.coldRamp || s.followerRamp) && (
        <div className="text-xs text-slate-500 flex flex-wrap items-center gap-x-2">
          <span>
            Semana <b className="text-slate-800">{s.week}</b> de la rampa (arrancó el {fmtDay(`${s.rampStartedOn}T12:00:00-03:00`)}).
            La última semana cargada vale de ahí en adelante.
          </span>
          <button type="button" className="underline" disabled={saving} onClick={() => save({}, { restartRamp: true })}>volver a la semana 1</button>
        </div>
      )}

      <label className="block text-xs">
        <span className="text-slate-500">Cuenta que manda (sin @)</span>
        <input className="input text-sm mt-0.5" placeholder={s.defaultAccount ?? 'ej. gymhero.fitness'}
          value={d.accountUsername} onChange={(e) => set('accountUsername', e.target.value)} />
      </label>

      <TemplateField
        label={<>Texto del DM en frío · variables: <code>{'{negocio}'}</code> <code>{'{app}'}</code></>}
        value={d.template} byDevice={byDevice}
        placeholder={`hola, como va? soy eric de ${s.displayName}. vi {negocio} y queria contarte algo que les puede servir, te paso info?`}
        render={(t) => t.replace(/\{negocio\}/g, 'Gimnasio Olimpo').replace(/\{app\}/g, s.displayName)}
        onChange={(v) => set('template', v)} />

      {/* Seguidores: solo con celular */}
      {byDevice && (
        <div className="border-t border-slate-100 pt-3 space-y-2">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <div className="text-sm font-medium">Escribirles a los seguidores</div>
              <div className="text-xs text-slate-500">
                El celular lee la lista de seguidores de la cuenta y les escribe, los más nuevos primero. No les escribe a
                clientes, a quien ya tiene una charla con nosotros ni a quien ya recibió un DM.
              </div>
            </div>
            <Switch on={d.followersEnabled} disabled={saving} title={d.followersEnabled ? 'Apagar' : 'Prender'}
              onClick={() => set('followersEnabled', !d.followersEnabled)} />
          </div>
          {d.followersEnabled && (
            <>
              <RampField label="Rampa a seguidores: DMs por día en la semana 1, 2, 3…" value={d.followerRamp} options={FOLLOWER_RAMPS}
                placeholder="ej. 20, 40, 50, 60" onChange={(v) => set('followerRamp', v)} />
              <TemplateField
                label={<>Texto para seguidores · variable: <code>{'{app}'}</code></>}
                value={d.followerTemplate} byDevice
                placeholder={`hola! gracias por seguirnos. soy eric de ${s.displayName}, tenes un gimnasio o entrenas en uno?`}
                render={(t) => t.replace(/\{app\}/g, s.displayName)}
                onChange={(v) => set('followerTemplate', v)} />
              <label className="block text-xs">
                <span className="text-slate-500">A estos nunca (equipo, cuentas propias): usuarios separados por coma o espacio</span>
                <input className="input text-sm mt-0.5" placeholder="_efrick, otra.cuenta"
                  value={d.followerExclude} onChange={(e) => set('followerExclude', e.target.value)} />
              </label>
              {s.followersEnabled && savedByDevice && (
                <div className="text-xs text-slate-500 flex flex-wrap items-center gap-x-2">
                  <span>{s.followerScanAt ? `Última lectura de seguidores: ${fmtDateTime(s.followerScanAt)}.` : 'Todavía no leyó los seguidores.'}</span>
                  <ActionLink label="leer ahora" disabled={saving || dirty}
                    run={() => api.post(`/ig-dm/${s.productKey}/scan-followers`)}
                    done="El celular los lee en su próxima consulta dentro del horario" />
                </div>
              )}
            </>
          )}
        </div>
      )}

      <div className="flex justify-end">
        <button type="button" className="btn-primary text-sm" disabled={!dirty || saving} onClick={() => save()}>
          {saving ? 'Guardando…' : 'Guardar'}
        </button>
      </div>

      {savedByDevice && <DeviceTools productKey={s.productKey} account={account ?? ''} />}
    </section>
  );
}

function Meter({ label, sent, cap, color, right }: { label: string; sent: number; cap: number; color: string; right: string }) {
  return (
    <div>
      <div className="flex justify-between gap-2 text-xs text-slate-500 mb-1">
        <span>{label}: <b className="text-slate-800 tabular-nums">{sent}</b> de {cap}</span>
        <span className="text-right">{right}</span>
      </div>
      <div className="h-2 rounded-full bg-slate-100 overflow-hidden">
        <div className={clsx('h-full', color)} style={{ width: `${cap ? Math.min(100, (sent / cap) * 100) : 0}%` }} />
      </div>
    </div>
  );
}

function RampField({ label, value, options, placeholder, onChange }: {
  label: string; value: string; options: { value: string; hint: string }[]; placeholder: string; onChange: (v: string) => void;
}) {
  const normalized = value.replace(/\s+/g, '');
  return (
    <div className="text-xs space-y-1">
      <span className="text-slate-500">{label}</span>
      <div className="flex flex-wrap items-center gap-1.5">
        <input className="input text-sm w-44 tabular-nums" placeholder={placeholder} value={value} onChange={(e) => onChange(e.target.value)} />
        {options.map((o) => (
          <button key={o.value} type="button" title={o.hint} onClick={() => onChange(o.value)}
            className={clsx('px-2.5 py-1 rounded-full border tabular-nums',
              normalized === o.value ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50')}>
            {rampLabel(o.value)}
          </button>
        ))}
      </div>
    </div>
  );
}

/** Texto con versiones (separadas por una línea con ---) y la vista previa de cómo sale. */
function TemplateField({ label, value, byDevice, placeholder, render, onChange }: {
  label: ReactNode; value: string; byDevice: boolean; placeholder: string;
  render: (t: string) => string; onChange: (v: string) => void;
}) {
  const variants = variantsOf(value);
  const warnings = copyWarnings(value, byDevice);
  return (
    <div className="text-xs space-y-1">
      <label className="block">
        <span className="text-slate-500">{label}</span>
        <textarea className="input text-sm mt-0.5 min-h-[88px]" value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />
      </label>
      <div className="text-slate-500">
        Para rotar varias versiones, separalas con una línea que tenga solo <code>---</code>: sale una al azar por persona.
        {variants.length > 1 && <b className="text-slate-700"> {variants.length} versiones cargadas.</b>}
      </div>
      {variants.map((v, i) => (
        <div key={i} className="bg-slate-50 border border-slate-200 rounded p-2 whitespace-pre-wrap text-slate-700">
          {byDevice ? foldForDevice(render(v)) : render(v)}
        </div>
      ))}
      {warnings.length > 0 && <div className="text-amber-700">Ojo: {warnings.join(', ')}.</div>}
    </div>
  );
}

/** Prueba contra una cuenta propia y bitácora de lo que se le pidió al celular. */
function DeviceTools({ productKey, account }: { productKey: string; account: string }) {
  const qc = useQueryClient();
  const [handle, setHandle] = useState('');
  const [text, setText] = useState('hola, esto es una prueba');
  const [sending, setSending] = useState(false);
  const { data: tasks } = useQuery({
    queryKey: ['ig-dm-tasks', productKey],
    queryFn: async () => (await api.get<DeviceTask[]>('/ig-dm/tasks', { params: { productKey, take: 12 } })).data,
    refetchInterval: 15_000,
  });

  async function test() {
    setSending(true);
    try {
      await api.post(`/ig-dm/${productKey}/test`, { handle, text });
      toast.success('El celular lo manda en su próxima consulta (menos de un minuto si no está con WhatsApp)');
      qc.invalidateQueries({ queryKey: ['ig-dm-tasks', productKey] });
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo pedir la prueba');
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="border-t border-slate-100 pt-3 space-y-3">
      <div className="text-xs space-y-1.5">
        <div className="text-sm font-medium text-slate-800">Probar en el celular</div>
        <div className="text-slate-500">
          Manda un DM de verdad{account ? ` desde @${account}` : ''} a la cuenta que pongas, con los mismos pasos que un DM real y
          sin contar para el tope. Usá una cuenta tuya.
        </div>
        <div className="flex flex-wrap gap-1.5">
          <input className="input text-sm w-40" placeholder="usuario (sin @)" value={handle} onChange={(e) => setHandle(e.target.value)} />
          <input className="input text-sm flex-1 min-w-[10rem]" placeholder="texto de la prueba" value={text} onChange={(e) => setText(e.target.value)} />
          <button type="button" className="btn-secondary text-sm" disabled={sending || !handle.trim() || !text.trim()} onClick={test}>
            {sending ? 'Pidiendo…' : 'Probar'}
          </button>
        </div>
      </div>

      <div className="text-xs space-y-1">
        <div className="text-sm font-medium text-slate-800">Movimientos del celular</div>
        {(tasks ?? []).length === 0 && <div className="text-slate-500">Todavía no se le pidió nada.</div>}
        {(tasks ?? []).map((t) => <TaskRow key={t.id} t={t} />)}
      </div>
    </div>
  );
}

function TaskRow({ t }: { t: DeviceTask }) {
  const state = t.status === 'ok' ? 'listo' : t.status === 'failed' ? 'falló' : t.status === 'running' ? 'en curso' : 'en espera';
  const tone = t.status === 'ok' ? 'text-emerald-700' : t.status === 'failed' ? 'text-rose-700' : 'text-slate-500';
  const why = t.error ? TASK_ERROR[t.error] ?? t.error : null;
  const head = (
    <span className="flex flex-wrap gap-x-2">
      <span className="tabular-nums text-slate-400">{fmtDateTime(t.createdAt)}</span>
      <span className="text-slate-700">{TASK_KIND[t.kind] ?? t.kind}{t.handle ? ` @${t.handle}` : ''}</span>
      <span className={tone}>
        {state}{why ? `: ${why}` : ''}{t.kind === 'followers' && t.collected != null ? ` (${t.collected} leídos)` : ''}
      </span>
    </span>
  );
  if (!t.text && !t.screen) return <div>{head}</div>;
  return (
    <details>
      <summary className="cursor-pointer">{head}</summary>
      <div className="mt-1 ml-3 space-y-1">
        {t.text && <div className="bg-slate-50 border border-slate-200 rounded p-2 text-slate-700">{t.text}</div>}
        {t.screen && (
          <div>
            <div className="text-slate-500">Lo que había en pantalla cuando falló:</div>
            <pre className="bg-slate-50 border border-slate-200 rounded p-2 text-[11px] text-slate-600 whitespace-pre-wrap max-h-56 overflow-auto">{t.screen}</pre>
          </div>
        )}
      </div>
    </details>
  );
}

function ActionLink({ label, run, done, disabled }: { label: string; run: () => Promise<unknown>; done: string; disabled?: boolean }) {
  const [busy, setBusy] = useState(false);
  return (
    <button type="button" className="underline disabled:opacity-50" disabled={busy || disabled}
      title={disabled ? 'Guardá los cambios primero' : undefined}
      onClick={async () => {
        setBusy(true);
        try { await run(); toast.success(done); }
        catch (e: any) { toast.error(e?.response?.data?.error ?? 'No se pudo'); }
        finally { setBusy(false); }
      }}>
      {label}
    </button>
  );
}

function NumField({ label, value, min, max, disabled, onChange }: {
  label: string; value: number; min: number; max: number; disabled?: boolean; onChange: (v: number) => void;
}) {
  return (
    <label className="block">
      <span className="text-slate-500">{label}</span>
      <input type="number" className="input text-sm mt-0.5 tabular-nums disabled:bg-slate-50 disabled:text-slate-400" min={min} max={max}
        value={value} disabled={disabled} onChange={(e) => onChange(Number(e.target.value))} />
    </label>
  );
}
