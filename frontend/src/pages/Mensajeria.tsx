import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Group = {
  key: string;
  label: string;
  hint: string;
  allowOutreach: boolean;
  allowFollowup: boolean;
  allowReply: boolean;
  queuedOutreach: number;
  queuedFollowup: number;
  leads: number;
};

type Kind = 'outreach' | 'followup' | 'reply';

const KINDS: { kind: Kind; title: string; hint: string }[] = [
  { kind: 'outreach', title: 'Mensajes nuevos', hint: 'El primer contacto a un lead al que nunca le escribimos.' },
  { kind: 'followup', title: 'Seguimiento', hint: 'Los pasos siguientes de la cadencia, re-enganches y avisos post-alta.' },
  { kind: 'reply', title: 'Respuestas del bot', hint: 'Cuando el lead escribe, el bot le contesta (o deja la sugerencia).' },
];

const field = (g: Group, k: Kind) =>
  k === 'outreach' ? g.allowOutreach : k === 'followup' ? g.allowFollowup : g.allowReply;

/**
 * Qué se manda y a quién. Los flags de "Motores automáticos" apagan un motor entero
 * (el de WhatsApp corta envíos Y respuestas); acá se corta más fino: por ORIGEN del
 * lead y por tipo de mensaje. Ej: apagar todos los mensajes nuevos y dejar prendidas
 * sólo las respuestas a los que llegaron por formulario de Meta.
 *
 * Lo que se apaga no se pierde: queda esperando en la cola y sale cuando se vuelve a
 * prender. Los mensajes que manda un humano a mano no se ven afectados.
 */
export default function Mensajeria() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({
    queryKey: ['messaging-policy'],
    queryFn: async () => (await api.get<Group[]>('/messaging-policy')).data,
  });
  const groups = data ?? [];

  const set = useMutation({
    mutationFn: async (v: { group: string; kind: Kind; enabled: boolean }) =>
      api.post(`/messaging-policy/${v.group}/${v.kind}`, { enabled: v.enabled }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['messaging-policy'] }),
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo cambiar'),
  });

  // Apagar/prender una columna entera (ej. "cortá todos los mensajes nuevos").
  async function toggleColumn(kind: Kind, enabled: boolean) {
    await Promise.all(groups.map((g) => api.post(`/messaging-policy/${g.key}/${kind}`, { enabled })));
    qc.invalidateQueries({ queryKey: ['messaging-policy'] });
    toast.success(enabled ? 'Prendido para todos los orígenes' : 'Apagado para todos los orígenes');
  }

  // Dejar SOLO este origen prendido (lo que más se usa: "sólo Meta Lead Ads").
  async function onlyThis(group: string) {
    await Promise.all(
      groups.flatMap((g) =>
        KINDS.map((k) => api.post(`/messaging-policy/${g.key}/${k.kind}`, { enabled: g.key === group })),
      ),
    );
    qc.invalidateQueries({ queryKey: ['messaging-policy'] });
    toast.success('Listo: quedó prendido sólo ese origen');
  }

  const anyOff = groups.some((g) => !g.allowOutreach || !g.allowFollowup || !g.allowReply);

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Mensajería</h1>
        <p className="text-sm text-slate-500 mt-1">
          Qué mensajes automáticos salen y para qué leads. Se corta por <b>origen</b> del lead y por
          <b> tipo de mensaje</b>. Lo que apagues no se pierde: queda esperando en la cola y sale cuando
          lo vuelvas a prender. Lo que mandás a mano desde Conversaciones nunca se frena.
        </p>
      </div>

      {anyOff && (
        <div className="rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          Hay mensajería apagada. Los leads siguen entrando y sus mensajes se acumulan en la cola.
        </div>
      )}

      {isLoading ? (
        <div className="text-sm text-slate-400">Cargando…</div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[720px] border-separate border-spacing-0 text-sm">
            <thead>
              <tr>
                <th className="text-left align-bottom pb-2 pr-3 w-[38%]">
                  <div className="font-semibold">Origen del lead</div>
                </th>
                {KINDS.map((k) => (
                  <th key={k.kind} className="align-bottom pb-2 px-3 text-center">
                    <div className="font-semibold">{k.title}</div>
                    <div className="text-[11px] font-normal text-slate-400 leading-tight mt-0.5">{k.hint}</div>
                    <div className="mt-1.5 flex items-center justify-center gap-2 text-[11px]">
                      <button className="text-slate-500 hover:underline" onClick={() => toggleColumn(k.kind, true)}>
                        prender todo
                      </button>
                      <span className="text-slate-300">|</span>
                      <button className="text-slate-500 hover:underline" onClick={() => toggleColumn(k.kind, false)}>
                        apagar todo
                      </button>
                    </div>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {groups.map((g) => (
                <tr key={g.key} className="border-t">
                  <td className="py-3 pr-3 border-t align-top">
                    <div className="font-medium">{g.label}</div>
                    <div className="text-[11px] text-slate-400 leading-tight">{g.hint}</div>
                    <div className="text-[11px] text-slate-400 mt-1">
                      {g.leads.toLocaleString('es-AR')} leads
                      {g.queuedOutreach + g.queuedFollowup > 0 && (
                        <>
                          {' · '}
                          <span className="text-amber-600">
                            {(g.queuedOutreach + g.queuedFollowup).toLocaleString('es-AR')} en cola
                          </span>
                        </>
                      )}
                      {' · '}
                      <button className="text-slate-500 hover:underline" onClick={() => onlyThis(g.key)}>
                        dejar sólo este
                      </button>
                    </div>
                  </td>
                  {KINDS.map((k) => (
                    <td key={k.kind} className="py-3 px-3 border-t text-center">
                      <div className="flex flex-col items-center gap-1">
                        <Switch
                          on={field(g, k.kind)}
                          disabled={set.isPending}
                          onClick={() => set.mutate({ group: g.key, kind: k.kind, enabled: !field(g, k.kind) })}
                          title={`${k.title} — ${g.label}`}
                        />
                        {k.kind !== 'reply' && !field(g, k.kind) && (
                          <span className="text-[10px] text-amber-600">
                            {(k.kind === 'outreach' ? g.queuedOutreach : g.queuedFollowup).toLocaleString('es-AR')} frenados
                          </span>
                        )}
                      </div>
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="text-xs text-slate-400 max-w-2xl leading-relaxed">
        Esto se aplica a los envíos por WhatsApp (línea propia o la de cada app), a los DMs de Instagram
        y a las respuestas automáticas. Los switches de <b>Motores automáticos</b> siguen mandando por
        encima: si el motor de WhatsApp está apagado, no sale nada aunque acá esté todo en verde.
      </div>
    </div>
  );
}
