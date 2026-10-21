// Motor de la extensión: por cada negocio de la cola busca el WhatsApp en este orden:
//   1) Instagram: el perfil (o, si no tenemos el handle, lo busca por nombre + ciudad) → link
//      wa.me / api.whatsapp de la bio o un número en el texto de la bio.
//   2) Su sitio web (el de la base o el link de la bio de IG) → links de WhatsApp / tel:.
//   3) Lo que quede sin número sale como "google" y se busca después en el servidor
//      (dotnet SalesHub.Api.dll audience-places --dataset …).
// Cada paso se agenda con chrome.alarms (sobrevive a que Chrome duerma el service worker). Trabaja en
// una ventana propia con dos pestañas fijas: Instagram (se navega desde adentro: buscador, clic en el
// resultado, scroll) y las webs. Todo el estado vive en chrome.storage.local.
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
  igAccount: '',        // cuenta de IG activa en la ventana de trabajo
  igTabId: 0,           // ventana de trabajo: pestaña fija de Instagram
  webTabId: 0,          // y la de las webs
  apiKey: '',           // clave de Marketing → Competencia → Públicos
  unsynced: []          // resultados que todavía no llegaron a sales-hub
};

const API = 'https://api.sales.efcloud.tech/api/audience';
const IDLE_MIN = 3;     // sin batch abierto en sales-hub: vuelve a preguntar cada 3 min

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
  heartbeat();
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

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

async function navigateTab(tabId, url, timeoutMs = 30_000) {
  const loaded = waitForTabLoad(tabId, timeoutMs);
  await chrome.tabs.update(tabId, { url });
  await loaded;
}

// Cuenta de IG activa → app: con esa cuenta se busca y se sigue solo a negocios de esa app. Con otra
// cuenta (o si no se reconoce) solo mira, no sigue a nadie.
const ACCOUNT_APP = { playcrewpadel: 'playcrew', 'gymhero.fitness': 'gymhero', 'turnos.pro': 'turnospro' };

const PLATFORM_IG = /^(agendapro|fresha|booksy|playtomic|atcsports|crossfy|wellhub|gympass|totalpass|fitpass|mindbody)/i;

// Ventana de trabajo: una pestaña fija de Instagram (se carga una vez y se navega desde adentro, como
// una persona) y otra para las webs de los negocios. Si alguien la cierra, se vuelve a abrir.
async function workTabs() {
  const s = await getState();
  const alive = async (id) => { if (!id) return null; try { return await chrome.tabs.get(id); } catch { return null; } };
  const ig = await alive(s.igTabId);
  const web = await alive(s.webTabId);
  if (ig && web && ig.windowId === web.windowId) return { ig: ig.id, web: web.id };
  for (const t of [ig, web]) if (t) await chrome.tabs.remove(t.id).catch(() => {});
  const win = await chrome.windows.create({ url: 'https://www.instagram.com/', focused: false, width: 1200, height: 900 });
  const igId = win.tabs[0].id;
  const w = await chrome.tabs.create({ windowId: win.id, url: 'about:blank', active: false });
  await chrome.tabs.update(igId, { active: true, autoDiscardable: false });
  await chrome.tabs.update(w.id, { autoDiscardable: false });
  await setState({ igTabId: igId, webTabId: w.id });
  await sleep(rand(4000, 7000));
  return { ig: igId, web: w.id };
}

// Corre una función en la pestaña. Las funciones de página son instantáneas: las esperas las hace la
// extensión, porque Chrome frena los timers de una pestaña que no está a la vista.
async function run(tabId, func, args = []) {
  for (let i = 0; ; i++) {
    try {
      const [res] = await chrome.scripting.executeScript({ target: { tabId }, func, args });
      return res?.result;
    } catch (e) {
      // Instagram a veces redirige justo al inyectar ("Frame with ID 0 was removed").
      if (i >= 2 || !/frame|removed|navigat|error page/i.test(String(e?.message || e))) throw e;
      await sleep(3000);
    }
  }
}

async function until(tabId, func, args, ok, timeoutMs = 15_000, everyMs = 500) {
  const end = Date.now() + timeoutMs;
  let r;
  while (Date.now() < end) {
    r = await run(tabId, func, args).catch(() => null);
    if (ok(r)) return r;
    await sleep(everyMs);
  }
  return r;
}

const normName = (s) => (s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '')
  .replace(/\b(crossfit|cross|box|gym|gimnasio|training|club|fitness|centro|studio|estudio|the|de|el|la)\b/g, ' ')
  .replace(/[^a-z0-9]/g, '');

// ---- Instagram, manejado desde la extensión ----

// Deja la pestaña en instagram.com con la barra cargada (solo recarga si no está en Instagram).
async function igReady(tab) {
  const t = await chrome.tabs.get(tab);
  if (!/^https:\/\/www\.instagram\.com\//.test(t.url || '')) await navigateTab(tab, 'https://www.instagram.com/').catch(() => {});
  const p = await until(tab, igPage, [], (r) => r && (r.nav || r.login || r.blocked), 20_000);
  return p?.login ? 'login' : p?.blocked ? 'blocked' : p?.nav ? 'ok' : 'error';
}

// Abre el panel "Buscar", escribe de a una letra y devuelve los resultados ({ handle, name }).
async function igSearch(tab, q) {
  const open = await until(tab, igSearchOpen, [], Boolean, 8000, 800);
  if (!open) return null;
  await sleep(rand(400, 1200));
  let typed = '';
  for (const ch of q) {
    typed += ch;
    await run(tab, igType, [typed]);
    await sleep(rand(60, 230));
  }
  await sleep(rand(2500, 4000));
  return (await run(tab, igResults)) || [];
}

// Busca la cuenta por nombre + ciudad (y solo nombre) en el buscador de IG.
async function igFind(tab, name, city) {
  const want = normName(name);
  if (want.length < 4) return { status: 'no_match' };
  let seen = 0;
  for (const q of [...new Set([`${name} ${city || ''}`.trim(), name])]) {
    const res = await igSearch(tab, q);
    if (res === null) return { status: 'error', error: 'no se abrió el buscador' };
    seen += res.length;
    const hit = res.find((r) => {
      const a = normName(r.name), b = normName(r.handle);
      return (a.length >= 4 && (a.includes(want) || want.includes(a))) || b.includes(want);
    });
    if (hit) return { status: 'ok', handle: hit.handle, results: res };
    await sleep(rand(1000, 2500));
  }
  // Sin ningún resultado no es "no existe": el buscador no respondió (p. ej. la pestaña estaba oculta).
  // Se marca error para que se reintente en vez de darlo por perdido.
  if (seen === 0) {
    const dbg = await run(tab, igSearchDebug).catch((e) => String(e?.message || e));
    return { status: 'error', error: `el buscador no trajo resultados (${dbg})` };
  }
  return { status: 'no_match', seen };
}

// Entra al perfil desde el buscador (si no aparece, por URL, como quien la pega), lee la bio, mira un
// rato (scroll) y, si toca, la sigue.
async function igProfile(tab, handle, doFollow, results) {
  const h = handle.toLowerCase();
  let res = results;
  if (!res?.some((r) => r.handle === h)) res = await igSearch(tab, handle);
  if (res?.some((r) => r.handle === h)) {
    await sleep(rand(500, 1500));
    await run(tab, igClick, [h]);
  } else {
    await navigateTab(tab, `https://www.instagram.com/${handle}/`).catch(() => {});
  }
  let p = await until(tab, igProfileRead, [h, true], (r) => r && r.status !== 'loading', 20_000, 600);
  if (p?.status === 'expanding') {
    await sleep(rand(800, 1500));
    p = await run(tab, igProfileRead, [h, false]);
  }
  if (!p || p.status === 'loading') return { status: 'error', error: 'el perfil no cargó' };
  if (['login', 'blocked', 'not_found'].includes(p.status)) return p;
  await sleep(rand(1500, 3500));
  await run(tab, igScroll, [rand(250, 700)]);
  await sleep(rand(1500, 4000));
  if (Math.random() < 0.5) {
    await run(tab, igScroll, [rand(300, 900)]);
    await sleep(rand(1000, 3000));
  }
  await run(tab, igScroll, [-10_000]);
  if (doFollow) {
    await sleep(rand(1500, 4000));
    const c = await run(tab, igFollowClick);
    if (c === 'clicked') {
      const f = await until(tab, igFollowState, [], (x) => x === 'followed' || x === 'blocked', 8000);
      p.follow = f === 'followed' || f === 'blocked' ? f : 'no_button';
    } else {
      p.follow = c || 'no_button';
    }
  }
  return p;
}

// De vez en cuando vuelve al inicio y baja un poco por el feed, como entre perfil y perfil.
async function igIdle(tab) {
  if (!(await run(tab, igHome).catch(() => false))) return;
  await sleep(rand(2000, 4000));
  for (let i = 0, n = Math.ceil(rand(1, 3)); i < n; i++) {
    await run(tab, igScroll, [rand(400, 1100)]).catch(() => {});
    await sleep(rand(2000, 6000));
  }
}

async function webVisit(tab, site) {
  try {
    await navigateTab(tab, /^https?:/i.test(site) ? site : `https://${site}`);
    await sleep(2500);
    return await run(tab, webRead);
  } finally {
    chrome.tabs.update(tab, { url: 'about:blank' }).catch(() => {});
  }
}

// ---- Funciones que corren DENTRO de las páginas (instantáneas, no pueden usar nada de afuera) ----

function igPage() {
  const body = document.body?.innerText || '';
  return {
    nav: !!document.querySelector('svg[aria-label]') && !!document.querySelector('a[href*="/direct/"]'),
    login: location.pathname.startsWith('/accounts/login') ||
      (!!document.querySelector('a[href*="/accounts/login"]') && !document.querySelector('a[href*="/direct/"]')),
    blocked: /(intent[aá] de nuevo más tarde|inténtalo de nuevo más tarde|try again later|restringimos|we restrict)/i.test(body)
  };
}

// La cuenta activa: el link con foto de la barra lateral (la que tiene "Inicio" y "Configuración").
function igWhoAmI() {
  let nav = document.querySelector('svg[aria-label="Inicio"], svg[aria-label="Home"]')?.closest('a');
  for (let i = 0; i < 10 && nav && !nav.querySelector('svg[aria-label="Configuración"], svg[aria-label="Settings"]'); i++) nav = nav.parentElement;
  const a = nav && [...nav.querySelectorAll('a[href^="/"]')].find((x) => x.querySelector('img'));
  const m = a?.getAttribute('href')?.match(/^\/([A-Za-z0-9._]+)\/$/);
  return m ? m[1].toLowerCase() : null;
}

function igSearchOpen() {
  const sel = 'input[aria-label="Términos de búsqueda"], input[aria-label="Search input"], input[placeholder="Buscar"], input[placeholder="Search"]';
  if (document.querySelector(sel)) return true;
  const icon = [...document.querySelectorAll('svg[aria-label]')].find((x) => /^(buscar|search)$/i.test(x.getAttribute('aria-label')));
  icon?.closest('a,[role="button"],[role="link"]')?.click();
  return false;
}

function igType(text) {
  const input = document.querySelector('input[aria-label="Términos de búsqueda"], input[aria-label="Search input"], input[placeholder="Buscar"], input[placeholder="Search"]');
  if (!input) return false;
  input.focus();
  // En una ventana de fondo focus() no dispara el evento y Instagram no abre la lista de resultados:
  // se lo avisamos a mano (React escucha focusin).
  if (!document.hasFocus() && text.length === 1) {
    input.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    input.dispatchEvent(new FocusEvent('focus'));
  }
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, text);
  input.dispatchEvent(new Event('input', { bubbles: true }));
  return true;
}

function igResults() {
  // Sin exigir role="link": según el ancho de la ventana Instagram arma la lista con otro marcado.
  const RESERVED = /^\/(popular|explore|reels?|direct|accounts|stories|p|tv|legal|about|web)\//;
  const input = document.querySelector('input[aria-label="Términos de búsqueda"], input[aria-label="Search input"], input[placeholder="Buscar"], input[placeholder="Search"]');
  if (!input) return [];
  // El panel del buscador: el primer ancestro del input que tiene links a perfiles (así no entra el feed de atrás).
  let box = input.parentElement;
  const isProfile = (a) => /^\/[A-Za-z0-9._]+\/$/.test(a.getAttribute('href')) && !RESERVED.test(a.getAttribute('href')) && (a.innerText || '').trim();
  while (box && box !== document.body && !box.querySelector('main') && ![...box.querySelectorAll('a[href^="/"]')].some(isProfile)) box = box.parentElement;
  if (!box || box === document.body || box.querySelector('main')) return [];
  const seen = new Map();
  for (const a of box.querySelectorAll('a[href^="/"]')) {
    const href = a.getAttribute('href');
    if (!/^\/[A-Za-z0-9._]+\/$/.test(href) || RESERVED.test(href)) continue;
    const lines = (a.innerText || '').split('\n').map((l) => l.trim()).filter(Boolean);
    if (!lines.length) continue;
    const handle = href.slice(1, -1).toLowerCase();
    if (!seen.has(handle)) seen.set(handle, { handle, name: lines[1] || '' });
  }
  return [...seen.values()].slice(0, 10);
}

// Qué había en pantalla cuando el buscador no trajo nada (para el log del batch).
function igSearchDebug() {
  const input = document.querySelector('input[aria-label="Términos de búsqueda"], input[aria-label="Search input"], input[placeholder="Buscar"], input[placeholder="Search"]');
  const links = [...document.querySelectorAll('a[href^="/"]')].filter((a) => /^\/[A-Za-z0-9._]+\/$/.test(a.getAttribute('href')));
  const panel = input?.closest('div[role="dialog"], section, nav')?.innerText || '';
  return `${location.pathname}, ${innerWidth}px, ${document.visibilityState}, foco ${document.hasFocus() ? 'sí' : 'no'}, buscador ${input ? `"${input.value}"` : 'no está'}, ${links.length} links de perfil, panel: ${panel.replace(/\s+/g, ' ').slice(0, 120)}`;
}

function igClick(handle) {
  const all = [...document.querySelectorAll(`a[href="/${handle}/" i]`)];
  const a = all.find((x) => x.offsetParent !== null) || all[0];
  if (!a) return false;
  a.click();
  return true;
}

function igScroll(dy) {
  if (dy < 0) window.scrollTo({ top: 0, behavior: 'smooth' });
  else window.scrollBy({ top: dy, behavior: 'smooth' });
  return true;
}

function igHome() {
  const icon = [...document.querySelectorAll('svg[aria-label]')].find((x) => /^(inicio|home)$/i.test(x.getAttribute('aria-label')));
  const a = icon?.closest('a');
  if (!a) return false;
  a.click();
  return true;
}

// Lee el perfil abierto: con expand abre la bio completa ("más") y avisa; si no, busca links de
// WhatsApp con número, un número en el texto y devuelve los links externos que vale la pena abrir.
function igProfileRead(handle, expand) {
  const body = document.body?.innerText || '';
  if (location.pathname.startsWith('/accounts/login')) return { status: 'login' };
  if (/(intent[aá] de nuevo más tarde|inténtalo de nuevo más tarde|try again later|restringimos|we restrict)/i.test(body)) return { status: 'blocked' };
  if (!location.pathname.toLowerCase().startsWith(`/${handle}/`)) return { status: 'loading' };
  if (/(esta página no está disponible|sorry, this page isn't available|page isn't available)/i.test(body)) return { status: 'not_found' };
  const header = document.querySelector('main header') || document.querySelector('header section');
  if (!header || !header.innerText.toLowerCase().includes(handle)) return { status: 'loading' };
  if (expand) {
    const more = [...header.querySelectorAll('div[role="button"], span')].find((e) => /^(más|more)$/i.test((e.innerText || '').trim()));
    if (more) { more.click(); return { status: 'expanding' }; }
  }
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
  const links = [...header.querySelectorAll('a[href]')].map((a) => unwrap(a.href)).filter((h) => /^https?:/i.test(h));
  const wa = links.map(waNumber).find(Boolean);
  if (wa) return { status: 'ok', phone: wa, source: 'instagram_link' };
  // Número en la bio: preferir la línea que habla de WhatsApp / teléfono.
  const lines = header.innerText.split('\n');
  const cand = [...lines.filter((l) => /(whats|wsp|wpp|wa\b|📲|📱|☎|📞|tel|cel|turnos|reservas)/i.test(l)), ...lines];
  for (const l of cand) {
    const m = l.match(/\+?\d[\d\s\-().]{7,}\d/);
    if (m && m[0].replace(/\D/g, '').length >= 8) return { status: 'ok', phone: m[0], source: 'instagram_bio' };
  }
  const external = [...new Set(links.filter((h) => !USELESS.test(h)))].sort((a, b) => rank(a) - rank(b));
  return { status: 'no_phone', links: external };
}

// Seguir: solo el botón del header (abajo están las "Sugerencias" con su propio "Seguir").
function igFollowClick() {
  const header = document.querySelector('main header') || document.querySelector('header section');
  if (!header) return 'no_button';
  const list = [...header.querySelectorAll('button, div[role="button"]')].map((el) => ({ el, text: (el.innerText || '').trim() }));
  if (list.some((b) => /^(siguiendo|following|solicitado|requested|pendiente)$/i.test(b.text))) return 'already';
  const btn = list.find((b) => /^(seguir|follow|seguir también|follow back)$/i.test(b.text));
  if (!btn) return 'no_button';
  btn.el.click();
  return 'clicked';
}

function igFollowState() {
  if (/(intent[aá] de nuevo más tarde|inténtalo de nuevo más tarde|try again later|restringimos|we restrict)/i.test(document.body?.innerText || '')) return 'blocked';
  const header = document.querySelector('main header') || document.querySelector('header section');
  const list = header ? [...header.querySelectorAll('button, div[role="button"]')].map((el) => (el.innerText || '').trim()) : [];
  return list.some((t) => /^(siguiendo|following|solicitado|requested|pendiente)$/i.test(t)) ? 'followed' : null;
}

// Busca WhatsApp / teléfono en una web (linktree y similares incluidos). En la página de un link de
// WhatsApp Business (wa.me/message/…) el número aparece en el texto.
function webRead() {
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
      body: JSON.stringify({ limit: 1, skip, excludeProducts: appsDone(s), onlyProducts: ACCOUNT_APP[s.igAccount] ? [ACCOUNT_APP[s.igAccount]] : s.onlyApps })
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

// Instalada "descomprimida", los archivos se leen del disco: si la carpeta trae otra versión
// (git pull o el .zip nuevo copiado encima), se recarga sola sin pasar por chrome://extensions.
async function reloadIfUpdated() {
  try {
    const disk = await fetch(chrome.runtime.getURL('manifest.json'), { cache: 'no-store' }).then((r) => r.json());
    if (disk.version && disk.version !== chrome.runtime.getManifest().version) chrome.runtime.reload();
  } catch { /* carpeta a medio copiar: se reintenta en el próximo heartbeat */ }
}

// Estado de la extensión a sales-hub (Públicos lo muestra): versión, cuenta, qué está haciendo.
async function heartbeat() {
  await reloadIfUpdated();
  const s = await getState();
  if (!s.apiKey) return;
  const now = Date.now();
  const state = !s.running ? 'pausada'
    : now < s.blockedUntil ? 'bloqueada'
    : now < s.restUntil ? 'descansando'
    : !inWindow(s) ? 'fuera de horario'
    : igCount(s) >= s.dailyCap ? 'tope del día'
    : s.queue.length === 0 ? 'esperando batch'
    : 'trabajando';
  try {
    await fetch(`${API}/finder-heartbeat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Audience-Key': s.apiKey },
      body: JSON.stringify({
        version: chrome.runtime.getManifest().version, account: s.igAccount || null, state,
        nextAt: s.nextAt ? new Date(s.nextAt).toISOString() : null,
        restUntil: s.restUntil > now ? new Date(s.restUntil).toISOString() : null,
        blockedUntil: s.blockedUntil > now ? new Date(s.blockedUntil).toISOString() : null,
        lastError: s.lastError || null, queue: s.queue.length, unsynced: s.unsynced.length,
        igToday: igCount(s), followsToday: followCount(s), current: s.queue[0]?.name || null
      })
    });
  } catch { /* sin red */ }
}

// ---- Un negocio por vez ----

async function lookup(item, s) {
  let followPending = ACCOUNT_APP[s.igAccount] === item.app && canFollow(s, item.app);
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

  const tabs = await workTabs();
  const profile = async (h, results) => {
    let p;
    try { p = await igProfile(tabs.ig, h, followPending, results); }
    catch (e) { p = { status: 'error', error: String(e?.message || e) }; }
    step('ig_profile', p?.status || 'error', p?.status === 'ok' ? `@${h} · ${p.source}` : p?.error ? `@${h} · ${p.error}` : `@${h}`);
    if (p?.follow) {
      step('ig_follow', p.follow, `@${h}`);
      followResult = p.follow;
      followPending = false;
    }
    return p;
  };
  const ready = async () => {
    igUsed = true;
    let st;
    try { st = await igReady(tabs.ig); } catch { st = 'error'; }
    return st;
  };

  // 1) Instagram (si el handle de la base ya no existe, se busca por nombre)
  let searchResults = null;
  if (handle) {
    const st = await ready();
    if (st === 'login' || st === 'blocked') return done({ status: st });
    const p = await profile(handle);
    if (p?.status === 'login' || p?.status === 'blocked') return done({ status: p.status });
    if (p?.status === 'ok') return done({ status: 'ok', phone: p.phone, source: p.source, instagram: handle, website });
    if (p?.status === 'not_found') handle = null;
    else igLinks = p?.links || [];
  }
  if (!handle && !igLinks.length) {
    const st = await ready();
    if (st === 'login' || st === 'blocked') return done({ status: st });
    let r;
    try { r = await igFind(tabs.ig, item.name, item.city || ''); }
    catch (e) { r = { status: 'error', error: String(e?.message || e) }; }
    step('ig_search', r?.status || 'error', r?.handle ? `"${item.name}" → @${r.handle}` : r?.error || `"${item.name}" · ${r?.seen ?? 0} resultados`);
    if (r?.status === 'error' && !website) return done({ status: 'error' });
    handle = r?.handle && r.handle !== (item.instagram || '').toLowerCase() ? r.handle : null;
    searchResults = r?.results || null;
  }
  if (handle && handle.toLowerCase() !== (item.instagram || '').toLowerCase()) {
    await sleep(rand(1500, 4000));
    const p = await profile(handle, searchResults);
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
      const w = await webVisit(tabs.web, site);
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

// Mira qué cuenta de IG está activa; si cambió, descarta lo que tenía en cola para otra app.
async function checkAccount() {
  try {
    const tabs = await workTabs();
    if ((await igReady(tabs.ig)) !== 'ok') return;
    const me = await run(tabs.ig, igWhoAmI);
    const s = await getState();
    if (!me || me === s.igAccount) return;
    const app = ACCOUNT_APP[me];
    await setState({ igAccount: me, queue: app ? s.queue.filter((q) => q.app === app) : s.queue });
  } catch { /* se vuelve a mirar en el próximo turno */ }
}

async function runOne() {
  {
    const s0 = await getState();
    if (s0.running && inWindow(s0) && Date.now() >= s0.restUntil && Date.now() >= s0.blockedUntil) await checkAccount();
  }
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
    if (r.igUsed && Math.random() < 0.25) await igIdle((await workTabs()).ig).catch(() => {});
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
  if (a.name === 'heartbeat') heartbeat();
});
chrome.alarms.create('heartbeat', { periodInMinutes: 2 });

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  (async () => {
    if (msg.type === 'start') {
      await setState({ running: true, pausedByUser: false, lastError: '' });
      await schedule(3_000);
    } else if (msg.type === 'pause') {
      await setState({ running: false, pausedByUser: true, nextAt: 0 });
      await chrome.alarms.clear(ALARM);
      heartbeat();
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
