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
  hourStart: 8,
  hourEnd: 23,          // exclusiva
  blockedUntil: 0,      // IG mostró "intentá más tarde"
  // Ráfagas como una persona: trabaja un rato (50-70 min) y descansa (20-40 min), al azar.
  workMin: 50,
  workMax: 70,
  restMin: 20,
  restMax: 40,
  sessionStart: 0,      // inicio de la ráfaga actual
  sessionLen: 0,        // ms que dura esta ráfaga
  restUntil: 0,         // descansando hasta
  nextAt: 0,
  lastError: '',
  igToday: { day: '', n: 0 },
  follow: true,         // además de buscar el número, seguir la cuenta (con su propio tope)
  followCap: 200,       // follows por día (Eric 21/10: su cuenta aguanta 200)
  followGapMin: 3,      // minutos mínimos entre follows (×1 a ×1,6 al azar)
  followToday: { day: '', n: 0 },
  lastFollowAt: 0,
  followBlockedUntil: 0,
  // Reparto por app: cada una tiene su tope diario dentro de los topes de la cuenta; cuando una llega,
  // se sigue con las otras. onlyApps = esta extensión trabaja solo para esas apps (un perfil de Chrome por app).
  appVisitCap: 85,
  appFollowCap: 65,
  onlyApps: [],
  appToday: { day: '', visits: {}, follows: {} },
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
const followCount = (s) => (s.followToday.day === todayKey() ? s.followToday.n : 0);
const APPS = ['turnospro', 'gymhero', 'playcrew'];
const appDay = (s) => (s.appToday.day === todayKey() ? s.appToday : { day: todayKey(), visits: {}, follows: {} });
const appVisits = (s, app) => appDay(s).visits[app] || 0;
const appFollows = (s, app) => appDay(s).follows[app] || 0;
const appDone = (s, app) => !!app && appVisits(s, app) >= s.appVisitCap;
const appsDone = (s) => APPS.filter((a) => appDone(s, a));
// ¿Toca seguir en este perfil? Tope diario, pausa mínima entre follows y sin bloqueo de follow vigente.
const canFollow = (s, app) => s.follow && followCount(s) < s.followCap && Date.now() >= s.followBlockedUntil &&
  (!app || appFollows(s, app) < s.appFollowCap) &&
  Date.now() - s.lastFollowAt >= s.followGapMin * 60_000 * rand(1, 1.6);

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
  // Se pide de a uno: con la cola vacía primero se pregunta a sales-hub, y solo si no hay pendientes
  // se espera IDLE_MIN.
  if (s.queue.length === 0) {
    await refill();
    if ((await getState()).queue.length === 0) delayMs = Math.max(delayMs, IDLE_MIN * 60_000);
  }
  const at = nextSlot(s, Math.max(Date.now() + delayMs, s.blockedUntil, s.restUntil));
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

const PLATFORM_IG = /^(agendapro|fresha|booksy|playtomic|atcsports|crossfy|wellhub|gympass|totalpass|fitpass|mindbody)/i;

async function inTab(url, func, args = []) {
  let tab;
  try {
    tab = await chrome.tabs.create({ url, active: false });
    await waitForTabLoad(tab.id);
    try {
      const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func, args });
      return res?.result;
    } catch (e) {
      // Instagram a veces redirige justo al inyectar ("Frame with ID 0 was removed"): un reintento.
      if (!/frame|removed|navigat/i.test(String(e?.message || e))) throw e;
      await new Promise((r) => setTimeout(r, 4000));
      const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func, args });
      return res?.result;
    }
  } finally {
    if (tab?.id) chrome.tabs.remove(tab.id).catch(() => {});
  }
}

// ---- Funciones que corren DENTRO de las páginas (no pueden usar nada de afuera) ----

// Busca la cuenta por nombre + ciudad con el panel "Buscar" de la web de IG, como una persona
// (el viejo /web/search/topsearch/ da 404 y /api/v1/users/search/ pide user-agent de la app).
async function igSearchOnPage(name, city) {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const norm = (s) => (s || '').toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/\b(crossfit|cross|box|gym|gimnasio|training|club|fitness|centro|studio|estudio|the|de|el|la)\b/g, ' ')
    .replace(/[^a-z0-9]/g, '');
  const RESERVED = /^\/(popular|explore|reels?|direct|accounts|stories|p|tv|legal|about|web)\//;
  for (let i = 0; i < 20 && !document.querySelector('svg[aria-label]'); i++) await sleep(500);
  if (location.pathname.startsWith('/accounts/login')) return { status: 'login' };
  const want = norm(name);
  if (want.length < 4) return { status: 'no_match' };
  const icon = [...document.querySelectorAll('svg[aria-label]')].find((x) => /^(buscar|search)$/i.test(x.getAttribute('aria-label')));
  icon?.closest('a,[role="button"],[role="link"]')?.click();
  let input;
  for (let i = 0; i < 10 && !input; i++) {
    await sleep(500);
    input = document.querySelector('input[aria-label="Términos de búsqueda"], input[aria-label="Search input"], input[placeholder="Buscar"], input[placeholder="Search"]');
  }
  if (!input) return { status: 'error' };
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  for (const q of [`${name} ${city || ''}`.trim(), name]) {
    input.focus();
    setter.call(input, q);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await sleep(3000 + Math.random() * 1500);
    const results = [...document.querySelectorAll('a[href^="/"][role="link"]')]
      .map((a) => ({ href: a.getAttribute('href'), lines: (a.innerText || '').split('\n') }))
      .filter((r) => /^\/[A-Za-z0-9._]+\/$/.test(r.href) && !RESERVED.test(r.href) && r.lines[0]);
    for (const r of results.slice(0, 8)) {
      const handle = r.href.slice(1, -1);
      const a = norm(r.lines[1]), b = norm(handle);
      if ((a.length >= 4 && (a.includes(want) || want.includes(a))) || b.includes(want)) return { status: 'ok', handle };
    }
  }
  return { status: 'no_match' };
}

// Lee el perfil: abre la bio completa ("más"), busca links de WhatsApp con número, un número en el
// texto y devuelve los links externos que vale la pena abrir (en orden de utilidad).
async function igProfileOnPage(doFollow) {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const FOLLOW = /^(seguir|follow|seguir también|follow back)$/i;
  const FOLLOWING = /^(siguiendo|following|solicitado|requested|pendiente)$/i;
  const headerButtons = (h) => [...h.querySelectorAll('button, div[role="button"]')].map((el) => ({ el, text: (el.innerText || '').trim() }));
  // Seguir: solo el botón del header (abajo están las "Sugerencias" con su propio "Seguir").
  const follow = async (h, res) => {
    if (!doFollow) return res;
    const list = headerButtons(h);
    if (list.some((b) => FOLLOWING.test(b.text))) return { ...res, follow: 'already' };
    const btn = list.find((b) => FOLLOW.test(b.text));
    if (!btn) return { ...res, follow: 'no_button' };
    await sleep(1500 + Math.random() * 2500);
    btn.el.click();
    for (let j = 0; j < 16; j++) {
      await sleep(500);
      if (BLOCK.test(document.body?.innerText || '')) return { ...res, follow: 'blocked' };
      if (headerButtons(h).some((b) => FOLLOWING.test(b.text))) return { ...res, follow: 'followed' };
    }
    return { ...res, follow: 'no_button' };
  };
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
    const m = href.match(/(?:wa\.me\/|whatsapp\.com\/send\/?\?(?:.*&)?phone=)\+?(\d{8,15})/i);
    return m ? m[1] : null;
  };
  // Links que no son la web del negocio: grupos de WhatsApp, Threads, Maps, otras cuentas de IG.
  const USELESS = /instagram\.com|threads\.(net|com)|chat\.whatsapp\.com|maps\.app\.goo\.gl|goo\.gl\/maps|google\.[a-z.]+\/maps|waze\.com/i;
  // wa.me/message/… y api.whatsapp.com/message/… (link de WhatsApp Business sin número): al abrirlo muestra el número.
  const rank = (h) => (/(wa\.me|whatsapp\.com)\/message\//i.test(h) ? 0 : /linktr\.ee|linkin\.bio|beacons\.ai|taplink|bio\.link|wa\.link/i.test(h) ? 1 : /facebook\.com/i.test(h) ? 3 : 2);
  for (let i = 0; i < 30; i++) {
    const body = document.body?.innerText || '';
    if (i >= 4 && loggedOut()) return { status: 'login' };
    if (BLOCK.test(body)) return { status: 'blocked' };
    if (GONE.test(body)) return { status: 'not_found' };
    const header = document.querySelector('main header') || document.querySelector('header section');
    if (header && header.innerText.length > 20) {
      await sleep(1200 + Math.random() * 1500);
      const more = [...header.querySelectorAll('div[role="button"], span')].find((e) => /^(más|more)$/i.test((e.innerText || '').trim()));
      if (more) { more.click(); await sleep(800 + Math.random() * 700); }
      const links = [...header.querySelectorAll('a[href]')].map((a) => unwrap(a.href)).filter((h) => /^https?:/i.test(h));
      const wa = links.map(waNumber).find(Boolean);
      if (wa) return follow(header, { status: 'ok', phone: wa, source: 'instagram_link' });
      // Número en la bio: preferir la línea que habla de WhatsApp / teléfono.
      const lines = header.innerText.split('\n');
      const cand = [...lines.filter((l) => /(whats|wsp|wpp|wa\b|📲|📱|☎|📞|tel|cel|turnos|reservas)/i.test(l)), ...lines];
      for (const l of cand) {
        const m = l.match(/\+?\d[\d\s\-().]{7,}\d/);
        if (m && m[0].replace(/\D/g, '').length >= 8) return follow(header, { status: 'ok', phone: m[0], source: 'instagram_bio' });
      }
      const external = [...new Set(links.filter((h) => !USELESS.test(h)))].sort((a, b) => rank(a) - rank(b));
      return follow(header, { status: 'no_phone', links: external });
    }
    await sleep(500);
  }
  return { status: 'error' };
}

// Busca WhatsApp / teléfono en una web (linktree y similares incluidos). En la página de un link de
// WhatsApp Business (wa.me/message/…) el número aparece en el texto.
async function webOnPage() {
  await new Promise((r) => setTimeout(r, 2500));
  const text = document.body?.innerText || '';
  if (/whatsapp\.com$/i.test(location.hostname)) {
    const m = text.match(/\+\d[\d\s\-()]{7,}\d/);
    return m ? { status: 'ok', phone: m[0], source: 'web_whatsapp' } : { status: 'no_phone' };
  }
  const hrefs = [...document.querySelectorAll('a[href]')].map((a) => a.href);
  for (const h of hrefs) {
    const m = h.match(/(?:wa\.me\/|whatsapp\.com\/send\/?\?(?:.*&)?phone=)\+?(\d{8,15})/i);
    if (m) return { status: 'ok', phone: m[1], source: 'web_whatsapp' };
  }
  const tel = hrefs.find((h) => h.startsWith('tel:') && h.replace(/\D/g, '').length >= 8);
  if (tel) return { status: 'ok', phone: decodeURIComponent(tel.slice(4)), source: 'web_tel' };
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
    const id = (u) => `${u.dataset}|${u.externalId}|${u.at}`;
    const sent = new Set(batch.map(id));
    await setState({ unsynced: fresh.unsynced.filter((u) => !sent.has(id(u))) });
  } catch { /* sin red: se reintenta con el próximo resultado */ }
}

// Pide a sales-hub el próximo negocio sin teléfono, de a uno: así el servidor decide siempre cuál
// sigue (prioridad, apps que ya llegaron a su tope, apps de esta extensión) y no hay cola local que filtrar.
async function refill() {
  const s = await getState();
  if (!s.apiKey || s.queue.length > 0) return;
  const skip = [...s.queue.map((q) => q.key), ...s.unsynced.map((u) => `${u.dataset}|${u.externalId}`)];
  try {
    const r = await fetch(`${API}/finder-queue`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Audience-Key': s.apiKey },
      body: JSON.stringify({ limit: 1, skip, excludeProducts: appsDone(s), onlyProducts: s.onlyApps })
    });
    if (r.status === 401) { await setState({ lastError: 'sales-hub rechazó la clave: copiala de nuevo desde Públicos.' }); return; }
    if (!r.ok) return;
    const items = await r.json();
    const fresh = await getState();
    const seen = new Set([...fresh.queue.map((q) => q.key), ...skip]);
    const add = items
      .map((i) => ({ key: `${i.dataset}|${i.externalId}`, app: i.productKey, dataset: i.dataset, externalId: i.externalId, name: i.name,
                     city: i.city || '', country: i.country || '', instagram: i.instagram || null, website: i.website || null }))
      .filter((i) => !seen.has(i.key));
    if (add.length) await setState({ queue: [...fresh.queue, ...add] });
  } catch { /* sin red: se reintenta en el próximo turno */ }
}

// ---- Un negocio por vez ----

async function lookup(item, s) {
  let followPending = canFollow(s, item.app);
  let followResult = null;
  // Cuentas de las plataformas (pie de las fichas de AgendaPro, Fresha…), no del negocio.
  let handle = PLATFORM_IG.test(item.instagram || '') ? null : item.instagram;
  let website = item.website;
  let igLinks = [];
  let igUsed = false;
  // Historial de lo que va pasando, con fecha: lo guarda sales-hub en audience_leads.finder_log.
  const steps = [];
  const step = (name, result, detail) => steps.push({ step: name, result, detail: detail || null, at: new Date().toISOString() });
  const host = (u) => { try { return new URL(/^https?:/i.test(u) ? u : `https://${u}`).hostname; } catch { return String(u).slice(0, 60); } };
  const done = (r) => ({ ...r, steps, igUsed, followResult });

  const profile = async (h) => {
    let p;
    try { p = await inTab(`https://www.instagram.com/${h}/`, igProfileOnPage, [followPending]); }
    catch (e) { p = { status: 'error', error: String(e?.message || e) }; }
    step('ig_profile', p?.status || 'error', p?.status === 'ok' ? `@${h} · ${p.source}` : p?.error ? `@${h} · ${p.error}` : `@${h}`);
    if (p?.follow) {
      step('ig_follow', p.follow, `@${h}`);
      followResult = p.follow;
      followPending = false;
    }
    return p;
  };

  // 1) Instagram (si el handle de la base ya no existe, se busca por nombre)
  if (handle) {
    igUsed = true;
    const p = await profile(handle);
    if (p?.status === 'login' || p?.status === 'blocked') return done({ status: p.status });
    if (p?.status === 'ok') return done({ status: 'ok', phone: p.phone, source: p.source, instagram: handle, website });
    if (p?.status === 'not_found') handle = null;
    else igLinks = p?.links || [];
  }
  if (!handle && !igLinks.length) {
    igUsed = true;
    let r;
    try { r = await inTab('https://www.instagram.com/', igSearchOnPage, [item.name, item.city || '']); }
    catch (e) { r = { status: 'error', error: String(e?.message || e) }; }
    step('ig_search', r?.status || 'error', r?.handle ? `"${item.name}" → @${r.handle}` : r?.error || `"${item.name}"`);
    if (r?.status === 'login' || r?.status === 'blocked') return done({ status: r.status });
    handle = r?.handle && r.handle !== item.instagram ? r.handle : null;
  }
  if (handle && handle !== item.instagram) {
    await new Promise((r) => setTimeout(r, rand(3000, 8000)));
    const p = await profile(handle);
    if (p?.status === 'login' || p?.status === 'blocked') return done({ status: p.status });
    if (p?.status === 'ok') return done({ status: 'ok', phone: p.phone, source: p.source, instagram: handle, website });
    igLinks = p?.links || [];
  }

  // 2) Web: primero un link de WhatsApp Business de la bio, después la web de la base y los demás links.
  const waMsg = igLinks.filter((h) => /(wa\.me|whatsapp\.com)\/message\//i.test(h));
  const sites = [...new Set([...waMsg, website, ...igLinks].filter(Boolean))].slice(0, 2);
  if (!sites.length) step('web', 'sin_web', 'no hay web ni links en la bio');
  for (const site of sites) {
    try {
      const w = await inTab(/^https?:/i.test(site) ? site : `https://${site}`, webOnPage);
      step('web', w?.status || 'error', w?.status === 'ok' ? `${host(site)} · ${w.source}` : host(site));
      if (w?.status === 'ok') return done({ status: 'ok', phone: w.phone, source: w.source, instagram: handle, website: website || site });
    } catch (e) {
      step('web', 'error', `${host(site)} · ${String(e?.message || e).slice(0, 80)}`);
    }
  }
  website = website || sites.find((x) => !/whatsapp|wa\.me/i.test(x)) || null;

  // 3) Queda para Google Maps (servidor)
  return done({ status: 'google', instagram: handle, website });
}

async function runOne() {
  await refill();
  const s = await getState();
  if (!s.running || s.queue.length === 0) return schedule(0);
  if (Date.now() < s.blockedUntil || Date.now() < s.restUntil || !inWindow(s) || igCount(s) >= s.dailyCap) return schedule(0);
  if (s.queue.length === 0 && (s.onlyApps.length ? s.onlyApps : APPS).every((a) => appDone(s, a))) return schedule(0);

  // Ráfaga nueva si no hay una en curso o la anterior quedó vieja (cerró el horario, se apagó la compu).
  const now = Date.now();
  if (!s.sessionStart || now - s.sessionStart > s.sessionLen + s.restMax * 60_000) {
    await setState({ sessionStart: now, sessionLen: rand(s.workMin, s.workMax) * 60_000 });
  }

  const item = s.queue[0];
  let r;
  let error = '';
  try {
    r = await lookup(item, s);
  } catch (e) {
    error = String(e?.message || e);
    r = { status: 'error', steps: [{ step: 'extension', result: 'error', detail: error.slice(0, 150), at: new Date().toISOString() }] };
  }

  const fresh = await getState();
  const patch = { lastError: error };
  if (r.igUsed) patch.igToday = { day: todayKey(), n: igCount(fresh) + 1 };
  if (item.app && (r.igUsed || r.followResult === 'followed')) {
    const d = appDay(fresh);
    patch.appToday = {
      day: d.day,
      visits: { ...d.visits, [item.app]: (d.visits[item.app] || 0) + (r.igUsed ? 1 : 0) },
      follows: { ...d.follows, [item.app]: (d.follows[item.app] || 0) + (r.followResult === 'followed' ? 1 : 0) }
    };
  }
  if (r.followResult === 'followed') {
    patch.followToday = { day: todayKey(), n: followCount(fresh) + 1 };
    patch.lastFollowAt = Date.now();
  } else if (r.followResult === 'blocked') {
    patch.followBlockedUntil = Date.now() + 24 * 3_600_000; // deja de seguir 24 h, sigue buscando números
  }
  const at = new Date().toISOString();
  const base = { dataset: item.dataset, externalId: item.externalId, steps: r.steps || [], at };
  let send = null;

  if (r.status === 'blocked') {
    patch.blockedUntil = Date.now() + 24 * 3_600_000; // se deja en la cola y se para 24 h
    send = { ...base, status: 'blocked' };
  } else if (r.status === 'login') {
    patch.running = false;
    patch.lastError = 'Instagram pide login: iniciá sesión en esta ventana de Chrome y dale Reanudar.';
    send = { ...base, status: 'login' };
  } else {
    const prev = fresh.results[item.key];
    const fails = (prev?.status === 'error' ? prev.fails || 1 : 0) + (r.status === 'error' ? 1 : 0);
    patch.queue = fresh.queue.filter((q) => q.key !== item.key);
    const retry = r.status === 'error' && fails < 2;
    if (retry) patch.queue.push(item); // un reintento al final de la cola
    const res = { phone: r.phone || null, source: r.source || null, instagram: r.instagram || item.instagram || null,
                  website: r.website || item.website || null, status: r.status, fails, at: Date.now() };
    patch.results = { ...fresh.results, [item.key]: res };
    send = { ...base, ...res, at, status: retry ? 'retry' : r.status };
  }
  patch.unsynced = [...fresh.unsynced, send].slice(-5000);
  // Terminó la ráfaga: descanso al azar antes de la próxima.
  if (fresh.sessionStart && Date.now() - fresh.sessionStart >= fresh.sessionLen) {
    patch.restUntil = Date.now() + rand(fresh.restMin, fresh.restMax) * 60_000;
    patch.sessionStart = 0;
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
// Al actualizar, la cola local se vacía y se vuelve a pedir a sales-hub, que decide el orden (los
// resultados ya conseguidos se mantienen).
chrome.runtime.onInstalled.addListener(async (d) => {
  if (d.reason === 'update') await setState({ queue: [] });
  await ensureRunning();
});
chrome.storage.onChanged.addListener((c) => { if (c.apiKey?.newValue) ensureRunning(); });
