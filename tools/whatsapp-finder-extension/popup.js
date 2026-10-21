const $ = (id) => document.getElementById(id);
const NUM_FIELDS = ['dailyCap', 'minGapSec', 'maxGapSec', 'hourStart', 'hourEnd', 'followCap', 'followGapMin',
  'workMin', 'workMax', 'restMin', 'restMax', 'appVisitCap', 'appFollowCap'];
const NUM_DEFAULTS = { dailyCap: 250, minGapSec: 25, maxGapSec: 70, hourStart: 9, hourEnd: 23, followCap: 200, followGapMin: 3,
  workMin: 50, workMax: 70, restMin: 20, restMax: 40, appVisitCap: 85, appFollowCap: 65 };
const APP_LABEL = { turnospro: 'TurnosPro', gymhero: 'GymHero', playcrew: 'PlayCrew' };
const LABEL = {
  ok: 'número', google: 'para Google', error: 'error'
};

const send = (type) => chrome.runtime.sendMessage({ type });
const fmtTime = (ms) => new Date(ms).toLocaleString('es-AR', { weekday: 'short', hour: '2-digit', minute: '2-digit' });


async function render() {
  const s = await chrome.storage.local.get(null);
  const results = Object.entries(s.results ?? {});
  const ig = s.igToday?.day === new Date().toLocaleDateString('sv') ? s.igToday.n : 0;
  $('sToday').textContent = `${ig}/${s.dailyCap ?? NUM_DEFAULTS.dailyCap}`;
  $('sQueue').textContent = (s.queue ?? []).length;
  $('sFound').textContent = results.filter(([, r]) => r.status === 'ok').length;
  $('sGoogle').textContent = results.filter(([, r]) => r.status === 'google').length;

  for (const f of NUM_FIELDS) if (document.activeElement !== $(f)) $(f).value = s[f] ?? NUM_DEFAULTS[f];
  $('follow').checked = s.follow ?? true;
  const only = s.onlyApps ?? [];
  document.querySelectorAll('.onlyApp').forEach((c) => { c.checked = only.includes(c.value); });
  const day = s.appToday?.day === new Date().toLocaleDateString('sv') ? s.appToday : { visits: {}, follows: {} };
  $('apps').textContent = 'Hoy: ' + Object.keys(APP_LABEL)
    .filter((a) => !only.length || only.includes(a))
    .map((a) => `${APP_LABEL[a]} ${day.visits[a] || 0}/${s.appVisitCap ?? 85} perfiles · ${day.follows[a] || 0}/${s.appFollowCap ?? 65} follows`)
    .join('  |  ') + (only.length ? '' : '  (sin tildar = todas)');
  const fToday = s.followToday?.day === new Date().toLocaleDateString('sv') ? s.followToday.n : 0;
  const fBlocked = (s.followBlockedUntil ?? 0) > Date.now();
  $('sFollow').textContent = fBlocked ? 'pausa' : `${fToday}/${s.followCap ?? NUM_DEFAULTS.followCap}`;

  if (document.activeElement !== $('apiKey')) $('apiKey').value = s.apiKey ?? '';
  const pending = (s.unsynced ?? []).length;
  $('sync').textContent = !s.apiKey ? 'sin clave: no se manda a sales-hub' : pending ? `${pending} sin mandar` : 'al día con sales-hub';

  const st = $('status');
  st.className = '';
  const blocked = (s.blockedUntil ?? 0) > Date.now();
  $('unblock').hidden = !blocked;
  if (s.lastError) { st.textContent = s.lastError; st.className = 'bad'; }
  else if (blocked) { st.textContent = `Instagram frenó. Retoma ${fmtTime(s.blockedUntil)}.`; st.className = 'warn'; }
  else if (!s.apiKey) st.textContent = 'Falta la clave de sales-hub';
  else if (!s.running) st.textContent = 'Pausado';
  else if ((s.restUntil ?? 0) > Date.now()) st.textContent = `Descansando hasta ${fmtTime(s.restUntil)}`;
  else if (!(s.queue ?? []).length) st.textContent = `Sin pendientes en sales-hub · vuelve a mirar ${s.nextAt ? fmtTime(s.nextAt) : 'en un rato'}`;
  else if (s.nextAt) st.textContent = `Andando · próximo ${fmtTime(s.nextAt)}`;
  else st.textContent = 'Andando';

  $('log').innerHTML = results.sort((a, b) => b[1].at - a[1].at).slice(0, 60)
    .map(([k, r]) => `<div>${fmtTime(r.at)} · ${k.split('|')[1]} · ${LABEL[r.status] ?? r.status}${r.phone ? ' · ' + r.source : ''}</div>`).join('');
}


$('start').onclick = async () => { await send('start'); render(); };
$('pause').onclick = async () => { await send('pause'); render(); };
$('unblock').onclick = async () => { await send('unblock'); render(); };
$('export').onclick = async () => {
  const { results = {} } = await chrome.storage.local.get('results');
  const rows = Object.entries(results).map(([key, r]) => {
    const [dataset, externalId] = key.split('|');
    return { dataset, externalId, ...r };
  });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([JSON.stringify(rows)], { type: 'application/json' }));
  a.download = `whatsapp-finder-${new Date().toLocaleDateString('sv')}.json`;
  a.click();
};

document.querySelectorAll('.onlyApp').forEach((c) => {
  c.onchange = async () => {
    const onlyApps = [...document.querySelectorAll('.onlyApp')].filter((x) => x.checked).map((x) => x.value);
    await chrome.storage.local.set({ onlyApps });
    render();
  };
});
$('follow').onchange = async () => { await chrome.storage.local.set({ follow: $('follow').checked }); render(); };
$('apiKey').onchange = async () => {
  await chrome.storage.local.set({ apiKey: $('apiKey').value.trim(), lastError: '' });
  await send('sync');
  render();
};

for (const f of NUM_FIELDS) {
  $(f).onchange = async () => {
    const v = Number($(f).value);
    if (!Number.isFinite(v)) return;
    await chrome.storage.local.set({ [f]: v });
    await send('reschedule');
    render();
  };
}

render();
setInterval(render, 3000);
