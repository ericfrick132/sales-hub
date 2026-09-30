import { useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { OnboardingAppConfig } from '../lib/types';

type Turn = {
  messages: string[]; intentKey: string | null; confident: boolean | null; decision: string | null;
  handoff: string | null; handoffSummary: string | null; finished: boolean;
};
type Line = { from: 'lead' | 'bot' | 'info'; text: string; meta?: string };

/**
 * Simulador del bot: escribís como si fueras el lead y el bot contesta con la misma lógica que en
 * producción (guion de la app, diccionario, pase a la persona y horarios reales de Calendly).
 * No sale nada por WhatsApp, no se avisa a nadie y la reserva en Calendly se simula.
 */
export default function ProbarBot() {
  const configs = useQuery({
    queryKey: ['onboarding-configs'],
    queryFn: async () => (await api.get<OnboardingAppConfig[]>('/onboarding-configs')).data,
  });
  const sellers = useQuery({
    queryKey: ['sellers-list'],
    queryFn: async () => (await api.get<{ id: string; displayName: string; isActive: boolean }[]>('/sellers')).data,
  });
  const calendly = useQuery({
    queryKey: ['calendly-event-types'],
    queryFn: async () => (await api.get<{ configured: boolean; items: { uri: string; name: string; duration: number }[] }>('/calendly/event-types')).data,
  });

  const [product, setProduct] = useState('gymhero');
  const [leadName, setLeadName] = useState('');
  const [handoff, setHandoff] = useState(true);
  const [handoffSeller, setHandoffSeller] = useState('');
  const [presentAs, setPresentAs] = useState('mateo');
  const [after, setAfter] = useState(2);
  const [eventType, setEventType] = useState('');

  const [leadId, setLeadId] = useState<string | null>(null);
  const [lines, setLines] = useState<Line[]>([]);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [finished, setFinished] = useState(false);
  const bottom = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const mateo = sellers.data?.find((s) => s.displayName.toLowerCase().startsWith('mateo'));
    if (mateo && !handoffSeller) setHandoffSeller(mateo.id);
  }, [sellers.data, handoffSeller]);
  useEffect(() => {
    if (calendly.data?.items?.length && !eventType) setEventType(calendly.data.items[0].uri);
  }, [calendly.data, eventType]);
  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'smooth' }); }, [lines]);
  // Por defecto hace TODAS las preguntas del guion de la app (la última suele ser la que saca el dolor).
  useEffect(() => {
    const n = configs.data?.find((c) => c.productKey === product)?.questions.length;
    if (n) setAfter(n);
  }, [configs.data, product]);

  const overrides = () => ({
    handoff, handoffSellerId: handoff ? handoffSeller || null : null, afterQuestions: after,
    presentAs, demoEventTypeUri: handoff ? eventType : '',
  });

  function addTurn(t: Turn) {
    const next: Line[] = t.messages.map((m) => ({ from: 'bot' as const, text: m }));
    const meta = [
      t.intentKey && `tipo: ${t.intentKey}${t.confident ? ' (seguro)' : ''}`,
      t.decision && `bot: ${t.decision}`,
      t.handoff && `motivo del pase: ${t.handoff}`,
    ].filter(Boolean).join(' · ');
    if (meta) next.unshift({ from: 'info', text: meta });
    if (t.handoffSummary) next.push({ from: 'info', text: 'Le llegaría a la persona por WhatsApp:', meta: t.handoffSummary });
    setLines((l) => [...l, ...next]);
    if (t.finished) setFinished(true);
  }

  async function start() {
    setBusy(true);
    try {
      if (leadId) await api.delete(`/sandbox/${leadId}`);
      setLines([]); setFinished(false);
      const r = (await api.post<{ leadId: string; turn: Turn }>('/sandbox/start', { productKey: product, leadName, overrides: overrides() })).data;
      setLeadId(r.leadId);
      setLines([{ from: 'info', text: 'Así arranca cuando entra alguien por el anuncio:' }]);
      addTurn(r.turn);
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo arrancar');
    } finally { setBusy(false); }
  }

  async function send() {
    if (!leadId || !text.trim()) return;
    const t = text.trim();
    setText('');
    setLines((l) => [...l, { from: 'lead', text: t }]);
    setBusy(true);
    try {
      addTurn((await api.post<Turn>(`/sandbox/${leadId}/message`, { text: t, overrides: overrides() })).data);
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'Falló');
    } finally { setBusy(false); }
  }

  const apps = (configs.data ?? []).filter((c) => c.questions.length > 0);

  return (
    <div className="space-y-4 max-w-5xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Probar el bot</h1>
        <p className="text-sm text-slate-500 mt-1">
          Escribí como si fueras el lead. El bot contesta con la misma lógica que en producción (guion de la app, pase a la
          persona y horarios reales de Calendly). <b>No sale nada por WhatsApp</b>, no se avisa a nadie y la reserva en
          Calendly se simula. Los ajustes de abajo son solo para la prueba: no cambian la configuración guardada.
        </p>
      </div>

      <div className="grid gap-4 lg:grid-cols-[300px_1fr]">
        <div className="rounded-xl border bg-white p-3 space-y-2 text-sm self-start">
          <label className="block space-y-0.5">
            <div className="text-slate-600">App</div>
            <select className="input w-full" value={product} onChange={(e) => setProduct(e.target.value)}>
              {apps.map((c) => <option key={c.productKey} value={c.productKey}>{c.displayName}</option>)}
            </select>
          </label>
          <label className="block space-y-0.5">
            <div className="text-slate-600">Nombre del lead (como lo ve el bot)</div>
            <input className="input w-full" placeholder="ej. Juan Perez" value={leadName} onChange={(e) => setLeadName(e.target.value)} />
          </label>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={handoff} onChange={(e) => setHandoff(e.target.checked)} />
            <span>Pre-calificar y pasar a una persona</span>
          </label>
          {handoff && (
            <>
              <label className="block space-y-0.5">
                <div className="text-slate-600">Pasar a</div>
                <select className="input w-full" value={handoffSeller} onChange={(e) => setHandoffSeller(e.target.value)}>
                  {(sellers.data ?? []).filter((s) => s.isActive).map((s) => <option key={s.id} value={s.id}>{s.displayName}</option>)}
                </select>
              </label>
              <div className="grid grid-cols-2 gap-2">
                <label className="space-y-0.5">
                  <div className="text-slate-600">Se presenta como</div>
                  <input className="input w-full" value={presentAs} onChange={(e) => setPresentAs(e.target.value)} />
                </label>
                <label className="space-y-0.5">
                  <div className="text-slate-600">Preguntas</div>
                  <input type="number" min={1} max={10} className="input w-full" value={after} onChange={(e) => setAfter(Number(e.target.value))} />
                </label>
              </div>
              <label className="block space-y-0.5">
                <div className="text-slate-600">Demo en Calendly</div>
                <select className="input w-full" value={eventType} onChange={(e) => setEventType(e.target.value)}>
                  <option value="">sin Calendly</option>
                  {(calendly.data?.items ?? []).map((ev) => <option key={ev.uri} value={ev.uri}>{ev.name} ({ev.duration} min)</option>)}
                </select>
              </label>
            </>
          )}
          <button className="btn-primary w-full" disabled={busy} onClick={start}>{leadId ? 'Empezar de nuevo' : 'Empezar'}</button>
          <div className="text-[11px] text-slate-400">
            Ideas para probar: contestar las preguntas normal; preguntar el precio a mitad; elegir un horario ("la primera",
            "el jueves", "10:30"); proponer otro horario; decir "ya tengo sistema"; mandar "sos un bot?".
          </div>
        </div>

        <div className="rounded-xl border bg-[#efeae2] flex flex-col h-[70vh]">
          <div className="flex-1 overflow-y-auto p-3 space-y-2">
            {lines.length === 0 && <div className="text-sm text-slate-500 text-center mt-10">Elegí la app y apretá Empezar.</div>}
            {lines.map((l, i) => l.from === 'info' ? (
              <div key={i} className="mx-auto max-w-[90%] rounded-lg bg-white/70 border px-2.5 py-1.5 text-[11px] text-slate-600">
                {l.text}
                {l.meta && <pre className="mt-1 whitespace-pre-wrap font-sans text-xs text-slate-800">{l.meta}</pre>}
              </div>
            ) : (
              <div key={i} className={`flex ${l.from === 'lead' ? 'justify-start' : 'justify-end'}`}>
                <div className={`max-w-[75%] rounded-lg px-3 py-1.5 text-sm shadow-sm whitespace-pre-wrap ${l.from === 'lead' ? 'bg-white' : 'bg-[#d9fdd3]'}`}>
                  {l.text}
                </div>
              </div>
            ))}
            <div ref={bottom} />
          </div>
          <div className="border-t bg-white p-2 flex gap-2">
            <input className="input flex-1" placeholder={leadId ? (finished ? 'ya se lo pasó a la persona (podés seguir escribiendo)' : 'escribí como el lead…') : 'apretá Empezar'}
              disabled={!leadId || busy} value={text} onChange={(e) => setText(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && send()} />
            <button className="btn-primary" disabled={!leadId || busy || !text.trim()} onClick={send}>Enviar</button>
          </div>
        </div>
      </div>
    </div>
  );
}
