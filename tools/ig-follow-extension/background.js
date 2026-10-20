// Motor de la extensión: cada follow se agenda con chrome.alarms (sobrevive a que Chrome
// duerma el service worker). Abre el perfil en una pestaña de fondo, hace click en "Seguir"
// y la cierra. Todo el estado vive en chrome.storage.local.

const ALARM = 'next-follow';

const DEFAULTS = {
  queue: [],            // handles pendientes, en orden
  log: [],              // { handle, result, at }
  running: false,
  dailyCap: 15,
  minGapMin: 8,
  maxGapMin: 30,
  hourStart: 10,        // hora local de la compu
  hourEnd: 22,          // exclusiva
  blockedUntil: 0,      // epoch ms; IG mostró "intentá más tarde"
  nextAt: 0,
  lastError: ''
};

async function getState() {
  return { ...DEFAULTS, ...(await chrome.storage.local.get(Object.keys(DEFAULTS))) };
}

const setState = (patch) => chrome.storage.local.set(patch);

const todayKey = (d = new Date()) => d.toLocaleDateString('sv'); // YYYY-MM-DD local

function followedToday(log) {
  const k = todayKey();
  return log.filter((e) => e.result === 'followed' && todayKey(new Date(e.at)) === k).length;
}

function inWindow(s, d = new Date()) {
  const h = d.getHours();
  if (s.hourStart === s.hourEnd) return true;
  return s.hourStart < s.hourEnd ? h >= s.hourStart && h < s.hourEnd : h >= s.hourStart || h < s.hourEnd;
}

const rand = (min, max) => min + Math.random() * (max - min);

// Próximo momento válido: dentro de la ventana horaria y sin pasar el tope del día.
function nextSlot(s, fromMs) {
  const d = new Date(fromMs);
  if (inWindow(s, d) && followedToday(s.log) < s.dailyCap) return fromMs;
  // Mañana (o hoy más tarde) a la hora de inicio, con unos minutos al azar.
  const start = new Date(d);
  start.setHours(s.hourStart, 0, 0, 0);
  if (start.getTime() <= fromMs || followedToday(s.log) >= s.dailyCap) start.setDate(start.getDate() + 1);
  return start.getTime() + rand(2, 25) * 60_000;
}

async function schedule(delayMs) {
  const s = await getState();
  if (!s.running || s.queue.length === 0) {
    await chrome.alarms.clear(ALARM);
    await setState({ nextAt: 0, running: s.queue.length > 0 && s.running });
    return;
  }
  const at = nextSlot(s, Math.max(Date.now() + delayMs, s.blockedUntil));
  await chrome.alarms.create(ALARM, { when: at });
  await setState({ nextAt: at });
}

function waitForTabLoad(tabId, timeoutMs = 30_000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      reject(new Error('timeout cargando el perfil'));
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

// Corre DENTRO de la página del perfil. Devuelve: followed | already | not_found | blocked | login | no_button
async function followOnPage() {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const FOLLOW = /^(seguir|follow|seguir también|follow back)$/i;
  const DONE = /^(siguiendo|following|solicitado|requested|pendiente)$/i;
  const BLOCK = /(intent[aá] de nuevo más tarde|inténtalo de nuevo más tarde|try again later|restringimos|we restrict)/i;
  const GONE = /(esta página no está disponible|sorry, this page isn't available|page isn't available)/i;

  // Solo los botones del header del perfil: debajo aparecen "Sugerencias" con su propio
  // "Seguir" y no hay que tocarlos.
  const buttons = () => {
    const header = document.querySelector('main header') || document.querySelector('header');
    if (!header) return [];
    return [...header.querySelectorAll('button, div[role="button"]')]
      .map((el) => ({ el, text: (el.innerText || '').trim() }));
  };

  // Sin sesión IG muestra el perfil igual, pero con links a "Iniciar sesión" y sin el inbox.
  const loggedOut = () =>
    location.pathname.startsWith('/accounts/login') ||
    (!!document.querySelector('a[href*="/accounts/login"]') && !document.querySelector('a[href*="/direct/"]'));

  for (let i = 0; i < 30; i++) {
    const body = document.body?.innerText || '';
    if (i >= 4 && loggedOut()) return 'login';
    if (GONE.test(body)) return 'not_found';
    const list = buttons();
    if (list.some((b) => DONE.test(b.text))) return 'already';
    const btn = list.find((b) => FOLLOW.test(b.text));
    if (btn) {
      await sleep(1500 + Math.random() * 2500); // mirar el perfil un momento antes del click
      btn.el.click();
      for (let j = 0; j < 16; j++) {
        await sleep(500);
        if (BLOCK.test(document.body?.innerText || '')) return 'blocked';
        if (buttons().some((b) => DONE.test(b.text))) return 'followed';
      }
      return BLOCK.test(document.body?.innerText || '') ? 'blocked' : 'no_button';
    }
    await sleep(500);
  }
  return 'no_button';
}

async function runOne() {
  const s = await getState();
  if (!s.running || s.queue.length === 0) return schedule(0);
  if (Date.now() < s.blockedUntil || !inWindow(s) || followedToday(s.log) >= s.dailyCap) return schedule(0);

  const handle = s.queue[0];
  let result = 'error';
  let error = '';
  let tab;
  try {
    tab = await chrome.tabs.create({ url: `https://www.instagram.com/${handle}/`, active: false });
    await waitForTabLoad(tab.id);
    const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: followOnPage });
    result = res?.result ?? 'error';
  } catch (e) {
    error = String(e?.message || e);
  } finally {
    if (tab?.id) chrome.tabs.remove(tab.id).catch(() => {});
  }

  const fresh = await getState();
  const patch = { lastError: error };
  const log = [...fresh.log, { handle, result, at: Date.now() }].slice(-5000);
  patch.log = log;

  if (result === 'blocked') {
    // IG frenó: se deja el handle en la cola y se para 24 h.
    patch.blockedUntil = Date.now() + 24 * 3_600_000;
  } else if (result === 'login') {
    patch.running = false;
    patch.lastError = 'Instagram pide login: iniciá sesión en esta ventana de Chrome y dale Reanudar.';
  } else if (result === 'error' || result === 'no_button') {
    // 3 seguidos sin botón/errores = no es el perfil, es la página (IG cambió el diseño, sesión
    // rara): se pausa sin descartar nada, para no quemar la lista entera.
    const last = log.slice(-3);
    if (last.length === 3 && last.every((e) => e.result === 'error' || e.result === 'no_button')) {
      patch.running = false;
      patch.lastError = 'No encuentro el botón "Seguir" en 3 perfiles seguidos. Pausado sin tocar la cola: revisá que Instagram cargue bien en este Chrome.';
    } else {
      // Reintenta una vez más tarde: lo manda al final de la cola, salvo que ya haya fallado antes.
      const fails = log.filter((e) => e.handle === handle && (e.result === 'error' || e.result === 'no_button')).length;
      patch.queue = fresh.queue.filter((h) => h !== handle);
      if (fails < 2) patch.queue.push(handle);
    }
  } else {
    patch.queue = fresh.queue.filter((h) => h !== handle);
  }
  await setState(patch);

  // Si no siguió a nadie (ya lo seguía / no existe) el próximo va más rápido.
  const [min, max] = result === 'followed' || result === 'blocked'
    ? [fresh.minGapMin, fresh.maxGapMin]
    : [0.5, 2];
  return schedule(rand(min, max) * 60_000);
}

chrome.alarms.onAlarm.addListener((a) => {
  if (a.name === ALARM) runOne();
});

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  (async () => {
    if (msg.type === 'start') {
      await setState({ running: true, lastError: '' });
      await schedule(5_000);
    } else if (msg.type === 'pause') {
      await setState({ running: false });
      await chrome.alarms.clear(ALARM);
      await setState({ nextAt: 0 });
    } else if (msg.type === 'reschedule') {
      const s = await getState();
      if (s.running) await schedule(rand(s.minGapMin, s.maxGapMin) * 60_000);
    } else if (msg.type === 'unblock') {
      await setState({ blockedUntil: 0 });
      const s = await getState();
      if (s.running) await schedule(5_000);
    }
    reply({ ok: true });
  })();
  return true;
});

// Si Chrome se reinició y la alarma se perdió, la vuelve a armar.
chrome.runtime.onStartup.addListener(async () => {
  const s = await getState();
  if (s.running && !(await chrome.alarms.get(ALARM))) await schedule(60_000);
});
