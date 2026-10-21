// Motor de la extensión: por cada negocio de la cola busca el WhatsApp en este orden:
//   1) Instagram: el perfil (o, si no tenemos el handle, lo busca por nombre + ciudad) → link
//      wa.me / api.whatsapp de la bio o un número en el texto de la bio.
//   2) Su sitio web (el de la base o el link de la bio de IG) → links de WhatsApp / tel:.
//   3) Lo que quede sin número sale como "google" y se busca después en el servidor
//      (dotnet SalesHub.Api.dll audience-places --dataset …).
// Cada paso se agenda con chrome.alarms (sobrevive a que Chrome duerma el service worker) y abre
// pestañas de fondo que cierra al terminar. Todo el estado vive en chrome.storage.local.
// Los números se guardan para públicos de Meta (tabla audience_leads): NO se les escribe.

const ALARM = 'next-lookup';

const DEFAULTS = {
  queue: [],            // { key, dataset, externalId, name, city, country, instagram, website }
  results: {},          // key → { phone, source, instagram, website, status, at }
  running: false,
  dailyCap: 250,        // perfiles de IG por día (las webs no cuentan)
  minGapSec: 25,
  maxGapSec: 70,
  hourStart: 9,
  hourEnd: 23,          // exclusiva
  blockedUntil: 0,      // IG mostró "intentá más tarde"
  nextAt: 0,
  lastError: '',
  igToday: { day: '', n: 0 },
  apiKey: '',           // clave de Marketing → Competencia → Públicos
  unsynced: []          // resultados que todavía no llegaron a sales-hub
};

const API = 'https://api.sales.efcloud.tech/api/audience';
const IDLE_MIN = 30;    // sin pendientes en sales-hub: vuelve a preguntar cada 30 min

async function getState() {
  return { ...DEFAULTS, ...(await chrome.storage.local.get(Object.keys(DEFAULTS))) };
}

const setState = (patch) => chrome.storage.local.set(patch);
const todayKey = (d = new Date()) => d.toLocaleDateString('sv');
const rand = (min, max) => min + Math.random() * (max - min);
const igCount = (s) => (s.igToday.day === todayKey() ? s.igToday.n : 0);

function inWindow(s, d = new Date()) {
  const h = d.getHours();
  if (s.hourStart === s.hourEnd) return true;
  return s.hourStart < s.hourEnd ? h >= s.hourStart && h < s.hourEnd : h >= s.hourStart || h < s.hourEnd;
}

function nextSlot(s, fromMs) {
  const d = new Date(fromMs);
  if (inWindow(s, d) && igCount(s) < s.dailyCap) return fromMs;
  const start = new Date(d);
  start.setHours(s.hourStart, 0, 0, 0);
  if (start.getTime() <= fromMs || igCount(s) >= s.dailyCap) start.setDate(start.getDate() + 1);
  return start.getTime() + rand(2, 20) * 60_000;
}

async function schedule(delayMs) {
  const s = await getState();
  if (!s.running) {
    await chrome.alarms.clear(ALARM);
    await setState({ nextAt: 0 });
    return;
  }
  if (s.queue.length === 0) delayMs = Math.max(delayMs, IDLE_MIN * 60_000);
  const at = nextSlot(s, Math.max(Date.now() + delayMs, s.blockedUntil));
  await chrome.alarms.create(ALARM, { when: at });
  await setState({ nextAt: at });
}

function waitForTabLoad(tabId, timeoutMs = 30_000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      reject(new Error('timeout cargando la página'));
    }, timeoutMs);
    function listener(id, info) {
      if (id === tabId && info.status === 'complete') {
        clearTimeout(timer);
        chrome.tabs.onUpdated.removeListener(listener);
        resolve();
      }
    }
    chrome.tabs.onUpdated.addListener(listener);
  });
}

async function inTab(url, func, args = []) {
  let tab;
  try {
    tab = await chrome.tabs.create({ url, active: false });
    await waitForTabLoad(tab.id);
    const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func, args });
    return res?.result;
  } finally {
    if (tab?.id) chrome.tabs.remove(tab.id).catch(() => {});
  }
}

// ---- Funciones que corren DENTRO de las páginas (no pueden usar nada de afuera) ----

// Busca la cuenta por nombre + ciudad con el buscador de IG (usa la sesión de la pestaña).
async function igSearchOnPage(name, city) {
  const norm = (s) => (s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '')
    .replace(/\b(crossfit|cross|box|gym|gimnasio|training|club|fitness|centro|studio|estudio|the|de|el|la)\b/g, ' ')
    .replace(/[^a-z0-9]/g, '');
  if (location.pathname.startsWith('/accounts/login')) return { status: 'login' };
  const want = norm(name);
  if (want.length < 4) return { status: 'no_match' };
  for (const q of [`${name} ${city || ''}`.trim(), name]) {
    const r = await fetch(`/web/search/topsearch/?context=blended&query=${encodeURIComponent(q)}`, { credentials: 'include' });
    if (r.status === 429) return { status: 'blocked' };
    if (!r.ok) continue;
    const j = await r.json().catch(() => null);
    for (const u of (j?.users || []).map((x) => x.user).slice(0, 8)) {
      const a = norm(u.full_name), b = norm(u.username);
      if ((a && (a.includes(want) || want.includes(a))) || b.includes(want)) return { status: 'ok', handle: u.username };
    }
    await new Promise((res) => setTimeout(res, 1500 + Math.random() * 2000));
  }
  return { status: 'no_match' };
}

// Lee el perfil: links de WhatsApp de la bio, número en el texto y el link externo.
async function igProfileOnPage() {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const BLOCK = /(intent[aá] de nuevo más tarde|inténtalo de nuevo más tarde|try again later|restringimos|we restrict)/i;
  const GONE = /(esta página no está disponible|sorry, this page isn't available|page isn't available)/i;
  const loggedOut = () => location.pathname.startsWith('/accounts/login') ||
    (!!document.querySelector('a[href*="/accounts/login"]') && !document.querySelector('a[href*="/direct/"]'));
  const unwrap = (href) => {
    try {
      const u = new URL(href);
      if (u.hostname === 'l.instagram.com') return decodeURIComponent(u.searchParams.get('u') || href);
    } catch { /* href raro */ }
    return href;
  };
  const waNumber = (href) => {
    const m = href.match(/(?:wa\.me\/|api\.whatsapp\.com\/send\/?\?(?:.*&)?phone=|whatsapp\.com\/send\/?\?(?:.*&)?phone=)\+?(\d{8,15})/i);
    return m ? m[1] : null;
  };
  for (let i = 0; i < 30; i++) {
    const body = document.body?.innerText || '';
    if (i >= 4 && loggedOut()) return { status: 'login' };
    if (BLOCK.test(body)) return { status: 'blocked' };
    if (GONE.test(body)) return { status: 'not_found' };
    const header = document.querySelector('main header') || document.querySelector('header section');
    if (header && header.innerText.length > 20) {
      await sleep(1200 + Math.random() * 1500);
      const links = [...header.querySelectorAll('a[href]')].map((a) => unwrap(a.href));
      const wa = links.map(waNumber).find(Boolean);
      if (wa) return { status: 'ok', phone: wa, source: 'instagram_link', links };
      const bio = header.innerText;
      // Número en la bio: preferir la línea que habla de WhatsApp / teléfono.
      const lines = bio.split('\n');
      const cand = [...lines.filter((l) => /(whats|wsp|wpp|wa\b|📲|📱|☎|📞|tel|cel|turnos|reservas)/i.test(l)), ...lines];
      for (const l of cand) {
        const m = l.match(/\+?\d[\d\s\-().]{7,}\d/);
        if (m && m[0].replace(/\D/g, '').length >= 8) return { status: 'ok', phone: m[0], source: 'instagram_bio', links };
      }
      const external = links.filter((h) => !/instagram\.com|facebook\.com\/instagram|threads\.net/i.test(h));
      return { status: 'no_phone', links: external };
    }
    await sleep(500);
  }
  return { status: 'error' };
}

// Busca WhatsApp / teléfono en una web (incluye linktree y similares).
async function webOnPage() {
  await new Promise((r) => setTimeout(r, 2500));
  const hrefs = [...document.querySelectorAll('a[href]')].map((a) => a.href);
  for (const h of hrefs) {
    const m = h.match(/(?:wa\.me\/|wa\.link\/|whatsapp\.com\/send\/?\?(?:.*&)?phone=)\+?(\d{8,15})/i);
    if (m) return { status: 'ok', phone: m[1], source: 'web_whatsapp' };
  }
  const tel = hrefs.find((h) => h.startsWith('tel:') && h.replace(/\D/g, '').length >= 8);
  if (tel) return { status: 'ok', phone: decodeURIComponent(tel.slice(4)), source: 'web_tel' };
  const text = document.body?.innerText || '';
  const m = text.match(/(?:whats\s?app|wsp|wpp)[^\d+]{0,20}(\+?\d[\d\s\-().]{7,}\d)/i);
  if (m) return { status: 'ok', phone: m[1], source: 'web_texto' };
  return { status: 'no_phone' };
}

// Manda los resultados pendientes a sales-hub; si falla quedan para el próximo intento.
async function sync() {
  const s = await getState();
  if (!s.apiKey || s.unsynced.length === 0) return;
  const batch = s.unsynced.slice(0, 200);
  try {
    const r = await fetch(`${API}/finder-results`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Audience-Key': s.apiKey },
      body: JSON.stringify(batch)
    });
    if (r.status === 401) { await setState({ lastError: 'sales-hub rechazó la clave: copiala de nuevo desde Públicos.' }); return; }
    if (!r.ok) return;
    const fresh = await getState();
    const sent = new Set(batch.map((b) => `${b.dataset}|${b.externalId}`));
    await setState({ unsynced: fresh.unsynced.filter((u) => !sent.has(`${u.dataset}|${u.externalId}`)) });
  } catch { /* sin red: se reintenta con el próximo resultado */ }
}

// Pide a sales-hub los próximos negocios sin teléfono cuando quedan pocos en la cola.
async function refill() {
  const s = await getState();
  if (!s.apiKey || s.queue.length >= 5) return;
  const skip = [...s.queue.map((q) => q.key), ...s.unsynced.map((u) => `${u.dataset}|${u.externalId}`)];
  try {
    const r = await fetch(`${API}/finder-queue`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Audience-Key': s.apiKey },
      body: JSON.stringify({ limit: 30, skip })
    });
    if (r.status === 401) { await setState({ lastError: 'sales-hub rechazó la clave: copiala de nuevo desde Públicos.' }); return; }
    if (!r.ok) return;
    const items = await r.json();
    const fresh = await getState();
    const seen = new Set([...fresh.queue.map((q) => q.key), ...skip]);
    const add = items
      .map((i) => ({ key: `${i.dataset}|${i.externalId}`, dataset: i.dataset, externalId: i.externalId, name: i.name,
                     city: i.city || '', country: i.country || '', instagram: i.instagram || null, website: i.website || null }))
      .filter((i) => !seen.has(i.key));
    if (add.length) await setState({ queue: [...fresh.queue, ...add] });
  } catch { /* sin red: se reintenta en el próximo turno */ }
}

// ---- Un negocio por vez ----

async function lookup(item, s) {
  let handle = item.instagram;
  let website = item.website;
  let igUsed = false;

  // 1) Instagram
  if (!handle) {
    igUsed = true;
    const r = await inTab('https://www.instagram.com/', igSearchOnPage, [item.name, item.city || '']);
    if (r?.status === 'login' || r?.status === 'blocked') return { status: r.status, igUsed };
    handle = r?.handle || null;
  }
  if (handle) {
    igUsed = true;
    await new Promise((r) => setTimeout(r, rand(3000, 8000)));
    const p = await inTab(`https://www.instagram.com/${handle}/`, igProfileOnPage);
    if (p?.status === 'login' || p?.status === 'blocked') return { status: p.status, igUsed };
    if (p?.status === 'ok') return { status: 'ok', phone: p.phone, source: p.source, instagram: handle, website, igUsed };
    website = website || (p?.links || [])[0] || null;
  }

  // 2) Web
  if (website) {
    try {
      const w = await inTab(/^https?:/i.test(website) ? website : `https://${website}`, webOnPage);
      if (w?.status === 'ok') return { status: 'ok', phone: w.phone, source: w.source, instagram: handle, website, igUsed };
    } catch { /* web caída: sigue a Google */ }
  }

  // 3) Queda para Google Maps (servidor)
  return { status: 'google', instagram: handle, website, igUsed };
}

async function runOne() {
  await refill();
  const s = await getState();
  if (!s.running || s.queue.length === 0) return schedule(0);
  if (Date.now() < s.blockedUntil || !inWindow(s) || igCount(s) >= s.dailyCap) return schedule(0);

  const item = s.queue[0];
  let r;
  let error = '';
  try {
    r = await lookup(item, s);
  } catch (e) {
    error = String(e?.message || e);
    r = { status: 'error' };
  }

  const fresh = await getState();
  const patch = { lastError: error };
  if (r.igUsed) patch.igToday = { day: todayKey(), n: igCount(fresh) + 1 };

  if (r.status === 'blocked') {
    patch.blockedUntil = Date.now() + 24 * 3_600_000; // se deja en la cola y se para 24 h
  } else if (r.status === 'login') {
    patch.running = false;
    patch.lastError = 'Instagram pide login: iniciá sesión en esta ventana de Chrome y dale Reanudar.';
  } else {
    const prev = fresh.results[item.key];
    const fails = (prev?.status === 'error' ? prev.fails || 1 : 0) + (r.status === 'error' ? 1 : 0);
    patch.queue = fresh.queue.filter((q) => q.key !== item.key);
    if (r.status === 'error' && fails < 2) patch.queue.push(item); // un reintento al final de la cola
    const res = { phone: r.phone || null, source: r.source || null, instagram: r.instagram || item.instagram || null,
                  website: r.website || item.website || null, status: r.status, fails, at: Date.now() };
    patch.results = { ...fresh.results, [item.key]: res };
    if (r.status !== 'error' || fails >= 2)
      patch.unsynced = [...fresh.unsynced, { dataset: item.dataset, externalId: item.externalId, ...res }].slice(-5000);
  }
  await setState(patch);
  await sync();

  const [min, max] = r.igUsed ? [fresh.minGapSec, fresh.maxGapSec] : [3, 10];
  return schedule(rand(min, max) * 1000);
}

chrome.alarms.onAlarm.addListener((a) => {
  if (a.name === ALARM) runOne();
});

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  (async () => {
    if (msg.type === 'start') {
      await setState({ running: true, pausedByUser: false, lastError: '' });
      await schedule(3_000);
    } else if (msg.type === 'pause') {
      await setState({ running: false, pausedByUser: true, nextAt: 0 });
      await chrome.alarms.clear(ALARM);
    } else if (msg.type === 'reschedule') {
      const s = await getState();
      if (s.running) await schedule(rand(s.minGapSec, s.maxGapSec) * 1000);
    } else if (msg.type === 'sync') {
      await sync();
    } else if (msg.type === 'unblock') {
      await setState({ blockedUntil: 0 });
      const s = await getState();
      if (s.running) await schedule(3_000);
    }
    reply({ ok: true });
  })();
  return true;
});

// Arranca sola en cuanto tiene la clave (salvo que la hayan pausado a mano) y rearma la alarma si
// Chrome se reinició o el service worker se recargó.
async function ensureRunning() {
  const s = await getState();
  if (!s.apiKey || s.pausedByUser) return;
  if (!s.running) await setState({ running: true });
  if (!(await chrome.alarms.get(ALARM))) await schedule(10_000);
}

chrome.runtime.onStartup.addListener(ensureRunning);
chrome.runtime.onInstalled.addListener(ensureRunning);
chrome.storage.onChanged.addListener((c) => { if (c.apiKey?.newValue) ensureRunning(); });
