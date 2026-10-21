const $ = (id) => document.getElementById(id);
const NUM_FIELDS = ['dailyCap', 'minGapSec', 'maxGapSec', 'hourStart', 'hourEnd', 'followCap', 'followGapMin',
  'workMin', 'workMax', 'restMin', 'restMax', 'appVisitCap', 'appFollowCap'];
const NUM_DEFAULTS = { dailyCap: 250, minGapSec: 25, maxGapSec: 70, hourStart: 8, hourEnd: 23, followCap: 200, followGapMin: 3,
  workMin: 50, workMax: 70, restMin: 20, restMax: 40, appVisitCap: 85, appFollowCap: 65 };
const APP_LABEL = { turnospro: 'TurnosPro', gymhero: 'GymHero', playcrew: 'PlayCrew' };
const send = (type) => chrome.runtime.sendMessage({ type });
const fmtTime = (ms) => new Date(ms).toLocaleTimeString('es-AR', { hour: '2-digit', minute: '2-digit' });
const isToday = (d) => d === new Date().toLocaleDateString('sv');

async function render() {
  const s = await chrome.storage.local.get(null);
  const ig = isToday(s.igToday?.day) ? s.igToday.n : 0;
  const fToday = isToday(s.followToday?.day) ? s.followToday.n : 0;
  const fBlocked = (s.followBlockedUntil ?? 0) > Date.now();
  $('sToday').textContent = `${ig}/${s.dailyCap ?? NUM_DEFAULTS.dailyCap}`;
  $('sFollow').textContent = fBlocked ? 'pausa' : `${fToday}/${s.followCap ?? NUM_DEFAULTS.followCap}`;
  $('sFound').textContent = Object.values(s.results ?? {}).filter((r) => r.status === 'ok').length;

  $('setup').hidden = !!s.apiKey;
  $('main').hidden = !s.apiKey;
  $('toggle').hidden = !s.apiKey;
  $('toggle').textContent = s.running ? 'Pausar' : 'Reanudar';

  for (const f of NUM_FIELDS) if (document.activeElement !== $(f)) $(f).value = s[f] ?? NUM_DEFAULTS[f];
  $('follow').checked = s.follow ?? true;
  const only = s.onlyApps ?? [];
  document.querySelectorAll('.onlyApp').forEach((c) => { c.checked = only.includes(c.value); });
  const day = isToday(s.appToday?.day) ? s.appToday : { visits: {}, follows: {} };
  $('apps').textContent = 'Hoy: ' + Object.keys(APP_LABEL)
    .filter((a) => !only.length || only.includes(a))
    .map((a) => `${APP_LABEL[a]} ${day.visits[a] || 0}/${day.follows[a] || 0}`).join(' · ') + ' (perfiles/follows)';
  const pending = (s.unsynced ?? []).length;
  $('sync').textContent = pending ? `${pending} resultados sin mandar a sales-hub` : '';

  const st = $('status');
  const blocked = (s.blockedUntil ?? 0) > Date.now();
  $('unblock').hidden = !blocked;
  const set = (text, cls = '') => { st.textContent = text; st.className = cls; };
  if (!s.apiKey) set('Sin conectar a sales-hub', 'warn');
  else if (s.lastError) set(s.lastError, 'bad');
  else if (blocked) set(`Instagram frenó · retoma ${fmtTime(s.blockedUntil)}`, 'warn');
  else if (!s.running) set('Pausada');
  else if ((s.restUntil ?? 0) > Date.now()) set(`Descansando hasta las ${fmtTime(s.restUntil)}`);
  else if (s.nextAt && s.nextAt - Date.now() > 15 * 60_000) set(`Retoma a las ${fmtTime(s.nextAt)}`);
  else if (!(s.queue ?? []).length) set('Esperando un batch (se arma en sales-hub → Públicos)');
  else set('Trabajando', 'ok');
  if (s.igAccount && s.apiKey) st.textContent += ` · @${s.igAccount}`;
}

$('toggle').onclick = async () => {
  const { running } = await chrome.storage.local.get('running');
  await send(running ? 'pause' : 'start');
  render();
};
$('unblock').onclick = async () => { await send('unblock'); render(); };

document.querySelectorAll('.onlyApp').forEach((c) => {
  c.onchange = async () => {
    const onlyApps = [...document.querySelectorAll('.onlyApp')].filter((x) => x.checked).map((x) => x.value);
    await chrome.storage.local.set({ onlyApps });
    render();
  };
});
$('follow').onchange = async () => { await chrome.storage.local.set({ follow: $('follow').checked }); render(); };
$('connect').onclick = () => chrome.tabs.create({ url: 'https://sales.efcloud.tech/' });

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
