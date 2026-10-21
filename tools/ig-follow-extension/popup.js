const $ = (id) => document.getElementById(id);
const NUM_FIELDS = ['dailyCap', 'minGapMin', 'maxGapMin', 'hourStart', 'hourEnd'];
const RESERVED = new Set(['p', 'reel', 'reels', 'explore', 'accounts', 'stories', 'tv', 'direct', 'share']);
const LABEL = {
  followed: 'seguido', already: 'ya lo seguía', not_found: 'no existe',
  blocked: 'BLOQUEO de IG', login: 'pide login', no_button: 'sin botón', error: 'error'
};

const send = (type) => chrome.runtime.sendMessage({ type });

// Si el texto trae links de instagram.com (JSON/CSV de una base con más columnas) se toman
// SOLO los links: partir por espacios convertiría "Club Padel Norte" en tres handles.
function parseHandles(raw) {
  const links = raw.match(/instagram\.com\/[A-Za-z0-9_.]+/gi);
  const parts = links ?? raw.split(/[\n,;\s]+/);
  const out = [];
  for (let part of parts) {
    part = part.trim();
    const i = part.toLowerCase().indexOf('instagram.com/');
    if (i >= 0) part = part.slice(i + 'instagram.com/'.length);
    const h = part.replace(/^@/, '').replace(/^\/+|\/+$/g, '').split(/[/?]/)[0].toLowerCase();
    if (/^[a-z0-9._]{1,30}$/.test(h) && !RESERVED.has(h)) out.push(h);
  }
  return out;
}

const fmtTime = (ms) => new Date(ms).toLocaleString('es-AR', { weekday: 'short', hour: '2-digit', minute: '2-digit' });

async function render() {
  const s = await chrome.storage.local.get(null);
  const log = s.log ?? [];
  const today = new Date().toLocaleDateString('sv');
  $('sToday').textContent = `${log.filter((e) => e.result === 'followed' && new Date(e.at).toLocaleDateString('sv') === today).length}/${s.dailyCap ?? 15}`;
  $('sQueue').textContent = (s.queue ?? []).length;
  $('sDone').textContent = log.filter((e) => e.result === 'followed').length;

  for (const f of NUM_FIELDS) if (document.activeElement !== $(f)) $(f).value = s[f] ?? { dailyCap: 15, minGapMin: 8, maxGapMin: 30, hourStart: 10, hourEnd: 22 }[f];

  const st = $('status');
  st.className = '';
  const blocked = (s.blockedUntil ?? 0) > Date.now();
  $('unblock').hidden = !blocked;
  if (s.lastError) { st.textContent = s.lastError; st.className = 'bad'; }
  else if (blocked) { st.textContent = `Instagram frenó los follows. Retoma ${fmtTime(s.blockedUntil)}.`; st.className = 'warn'; }
  else if (!s.running) st.textContent = (s.queue ?? []).length ? 'Pausado' : 'Cola vacía';
  else if (s.nextAt) st.textContent = `Andando · próximo follow ${fmtTime(s.nextAt)}`;
  else st.textContent = 'Andando';

  $('log').innerHTML = log.slice(-60).reverse()
    .map((e) => `<div>${fmtTime(e.at)} · @${e.handle} · ${LABEL[e.result] ?? e.result}</div>`).join('');
}

async function addHandles(raw) {
  const s = await chrome.storage.local.get(['queue', 'log']);
  const queue = s.queue ?? [];
  // No re-encolar a quien ya está en la cola o ya se procesó (seguido, ya seguía, no existe).
  const seen = new Set([...queue, ...(s.log ?? []).filter((e) => e.result !== 'error' && e.result !== 'no_button').map((e) => e.handle)]);
  let added = 0;
  for (const h of parseHandles(raw)) {
    if (seen.has(h)) continue;
    seen.add(h);
    queue.push(h);
    added++;
  }
  await chrome.storage.local.set({ queue });
  $('status').textContent = `${added} agregados a la cola.`;
  setTimeout(render, 1500);
}

$('start').onclick = async () => { await send('start'); render(); };
$('pause').onclick = async () => { await send('pause'); render(); };
$('unblock').onclick = async () => { await send('unblock'); render(); };
$('add').onclick = async () => { await addHandles($('handles').value); $('handles').value = ''; };
$('file').onchange = async (e) => {
  const file = e.target.files?.[0];
  if (file) await addHandles(await file.text());
  e.target.value = '';
};
$('clear').onclick = async () => {
  if (confirm('¿Vaciar la cola? El log se mantiene.')) { await chrome.storage.local.set({ queue: [] }); render(); }
};
$('export').onclick = async () => {
  const { log = [] } = await chrome.storage.local.get('log');
  const csv = 'handle,resultado,fecha\n' + log.map((e) => `${e.handle},${e.result},${new Date(e.at).toISOString()}`).join('\n');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }));
  a.download = `ig-follow-log-${new Date().toLocaleDateString('sv')}.csv`;
  a.click();
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
