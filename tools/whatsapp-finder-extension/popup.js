const $ = (id) => document.getElementById(id);
const NUM_FIELDS = ['dailyCap', 'minGapSec', 'maxGapSec', 'hourStart', 'hourEnd'];
const NUM_DEFAULTS = { dailyCap: 250, minGapSec: 25, maxGapSec: 70, hourStart: 9, hourEnd: 23 };
const LABEL = {
  ok: 'número', google: 'para Google', error: 'error'
};

const send = (type) => chrome.runtime.sendMessage({ type });
const fmtTime = (ms) => new Date(ms).toLocaleString('es-AR', { weekday: 'short', hour: '2-digit', minute: '2-digit' });

function handleOf(v) {
  if (!v) return null;
  const m = String(v).match(/instagram\.com\/([A-Za-z0-9_.]+)/i);
  const h = (m ? m[1] : String(v)).replace(/^@/, '').replace(/\.+$/, '').toLowerCase();
  return /^[a-z0-9._]{1,30}$/.test(h) && !['p', 'reel', 'reels', 'explore', 'accounts'].includes(h) ? h : null;
}

async function render() {
  const s = await chrome.storage.local.get(null);
  const results = Object.entries(s.results ?? {});
  const ig = s.igToday?.day === new Date().toLocaleDateString('sv') ? s.igToday.n : 0;
  $('sToday').textContent = `${ig}/${s.dailyCap ?? NUM_DEFAULTS.dailyCap}`;
  $('sQueue').textContent = (s.queue ?? []).length;
  $('sFound').textContent = results.filter(([, r]) => r.status === 'ok').length;
  $('sGoogle').textContent = results.filter(([, r]) => r.status === 'google').length;

  for (const f of NUM_FIELDS) if (document.activeElement !== $(f)) $(f).value = s[f] ?? NUM_DEFAULTS[f];

  if (document.activeElement !== $('apiKey')) $('apiKey').value = s.apiKey ?? '';
  const pending = (s.unsynced ?? []).length;
  $('sync').textContent = !s.apiKey ? 'sin clave: no se manda a sales-hub' : pending ? `${pending} sin mandar` : 'al día con sales-hub';

  const st = $('status');
  st.className = '';
  const blocked = (s.blockedUntil ?? 0) > Date.now();
  $('unblock').hidden = !blocked;
  if (s.lastError) { st.textContent = s.lastError; st.className = 'bad'; }
  else if (blocked) { st.textContent = `Instagram frenó. Retoma ${fmtTime(s.blockedUntil)}.`; st.className = 'warn'; }
  else if (!s.running) st.textContent = (s.queue ?? []).length ? 'Pausado' : 'Cola vacía';
  else if (s.nextAt) st.textContent = `Andando · próximo ${fmtTime(s.nextAt)}`;
  else st.textContent = 'Andando';

  $('log').innerHTML = results.sort((a, b) => b[1].at - a[1].at).slice(0, 60)
    .map(([k, r]) => `<div>${fmtTime(r.at)} · ${k.split('|')[1]} · ${LABEL[r.status] ?? r.status}${r.phone ? ' · ' + r.source : ''}</div>`).join('');
}

async function addItems(raw) {
  let rows;
  try { rows = JSON.parse(raw); } catch { $('status').textContent = 'El archivo no es JSON.'; return; }
  if (!Array.isArray(rows)) rows = Object.values(rows).find(Array.isArray) ?? [];
  const s = await chrome.storage.local.get(['queue', 'results']);
  const queue = s.queue ?? [];
  const done = s.results ?? {};
  const seen = new Set(queue.map((q) => q.key));
  let added = 0;
  for (const r of rows) {
    if (!r.dataset || r.externalId == null || !r.name || r.phone) continue;
    const key = `${r.dataset}|${r.externalId}`;
    if (seen.has(key) || (done[key] && done[key].status !== 'error')) continue;
    seen.add(key);
    queue.push({ key, dataset: r.dataset, externalId: String(r.externalId), name: r.name, city: r.city || '',
                 country: r.country || '', instagram: handleOf(r.instagram), website: r.website || null });
    added++;
  }
  await chrome.storage.local.set({ queue });
  $('status').textContent = `${added} agregados a la cola.`;
  setTimeout(render, 1500);
}

$('start').onclick = async () => { await send('start'); render(); };
$('pause').onclick = async () => { await send('pause'); render(); };
$('unblock').onclick = async () => { await send('unblock'); render(); };
$('file').onchange = async (e) => {
  const file = e.target.files?.[0];
  if (file) await addItems(await file.text());
  e.target.value = '';
};
$('clear').onclick = async () => {
  if (confirm('¿Vaciar la cola? Los resultados se mantienen.')) { await chrome.storage.local.set({ queue: [] }); render(); }
};
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
