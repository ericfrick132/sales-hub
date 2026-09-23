/**
 * Lector de chats de WhatsApp: vincula un celular como dispositivo más, agarra el historial
 * que WhatsApp le manda y devuelve la lista de conversaciones CON EL TELÉFONO REAL de cada una.
 *
 * Por qué existe un servicio aparte y no lo hace la API: el protocolo de WhatsApp Web solo
 * existe en Node (Baileys). La Evolution del droplet ya es eso, pero empaqueta baileys
 * 7.0.0-rc.9, que al sincronizar el historial guarda el LID (un id opaco, no un teléfono) y
 * TIRA el número — por eso de un celu con 1.234 chats solo 185 tenían a quién llamar. Desde
 * baileys 7.0.0-rc13 el history sync sí lo expone: cada conversación trae `pnJid` y además
 * llega la tabla `phoneNumberToLidMappings` (ver src/Utils/history.ts). Este servicio corre esa
 * versión sin tocar la Evolution que mueve el tráfico de las 6 apps.
 *
 * Solo LEE: no manda mensajes y no se pone online (markOnlineOnConnect:false), así el celular
 * sigue recibiendo sus notificaciones. Cuando termina, cierra la sesión y borra las credenciales.
 *
 * La maneja la API .NET (PhoneLinesController), que es la única que conoce READER_KEY.
 */

const express = require('express');
const fs = require('fs');
const path = require('path');
const P = require('pino');
const QR = require('qrcode');
const {
  default: makeWASocket,
  useMultiFileAuthState,
  fetchLatestBaileysVersion,
  DisconnectReason,
  Browsers,
} = require('baileys');

const PORT = process.env.PORT || 8090;
const API_URL = process.env.API_URL || 'http://saleshub-api:8080';
const READER_KEY = process.env.READER_KEY || '';
const DATA_DIR = process.env.DATA_DIR || '/data';
/** Mensajes de contexto por chat (los más nuevos): para llamar alcanza con saber de qué venían hablando. */
const MESSAGES_PER_CHAT = 10;
/** El historial llega en tandas; si pasa este rato sin ninguna, se da por completo. */
const IDLE_MS = 90_000;
/** Un escaneo que nadie completa no puede quedar colgado para siempre. */
const SESSION_MAX_MS = 60 * 60_000;

const log = (...a) => console.log(new Date().toISOString(), ...a);
const digits = (s) => (s || '').replace(/\D/g, '');
const userOf = (jid) => (jid || '').split('@')[0].split(':')[0];

/** lineId → sesión en curso. */
const sessions = new Map();

function publicState(s) {
  return {
    state: s.state,
    qrBase64: s.qrBase64 || null,
    chats: s.chats.size,
    resolved: [...s.chats.values()].filter((c) => phoneFor(s, c)).length,
    batches: s.batches,
    progress: s.progress,
    error: s.error || null,
    result: s.result || null,
    startedAt: s.startedAt,
  };
}

function phoneFor(s, c) {
  if (c.phone) return c.phone;
  if (c.jid?.endsWith('@s.whatsapp.net')) return digits(userOf(c.jid));
  const mapped = s.lidToPhone.get(userOf(c.jid));
  return mapped ? digits(mapped) : null;
}

/** Texto del mensaje; los que no son texto dejan un marcador para que se entienda el hilo. */
function textOf(m) {
  const msg = m?.message;
  if (!msg) return null;
  const inner = msg.ephemeralMessage?.message || msg.viewOnceMessage?.message || msg;
  if (inner.conversation) return inner.conversation;
  if (inner.extendedTextMessage?.text) return inner.extendedTextMessage.text;
  if (inner.imageMessage) return inner.imageMessage.caption || '[imagen]';
  if (inner.videoMessage) return inner.videoMessage.caption || '[video]';
  if (inner.audioMessage) return '[nota de voz]';
  if (inner.documentMessage) return inner.documentMessage.fileName ? `[archivo: ${inner.documentMessage.fileName}]` : '[archivo]';
  if (inner.stickerMessage) return '[sticker]';
  if (inner.locationMessage) return '[ubicación]';
  if (inner.contactMessage || inner.contactsArrayMessage) return '[contacto]';
  return null;
}

function remember(s, jid, patch) {
  if (!jid || jid.endsWith('@g.us') || jid === 'status@broadcast' || jid.endsWith('@newsletter')) return null;
  const cur = s.chats.get(jid) || { jid, name: null, phone: null, lastAt: null, messages: [] };
  for (const [k, v] of Object.entries(patch)) if (v != null && v !== '') cur[k] = v;
  s.chats.set(jid, cur);
  return cur;
}

function addMessage(s, jid, m) {
  const entry = remember(s, jid, {});
  if (!entry) return;
  const text = textOf(m);
  if (!text) return;
  const at = Number(m.messageTimestamp) || 0;
  entry.messages.push({ text, fromMe: !!m.key?.fromMe, at, id: m.key?.id || null });
  entry.messages.sort((a, b) => b.at - a.at);
  if (entry.messages.length > MESSAGES_PER_CHAT) entry.messages.length = MESSAGES_PER_CHAT;
  if (!entry.lastAt || at > entry.lastAt) entry.lastAt = at;
}

/** Los chats listos para el CRM: con número y con conversación. */
function snapshot(s) {
  const byPhone = new Map();
  for (const c of s.chats.values()) {
    const phone = phoneFor(s, c);
    if (!phone || phone.length < 8 || !c.messages.length) continue;
    const row = {
      phone,
      name: c.name || null,
      lastMessageAt: c.lastAt ? new Date(c.lastAt * 1000).toISOString() : null,
      messages: c.messages
        .slice()
        .sort((a, b) => a.at - b.at)
        .map((m) => ({ text: m.text, fromMe: m.fromMe, at: new Date(m.at * 1000).toISOString(), id: m.id })),
    };
    // Un mismo contacto puede venir por LID y por número: se juntan.
    const prev = byPhone.get(phone);
    if (!prev) { byPhone.set(phone, row); continue; }
    prev.name = prev.name || row.name;
    prev.messages = [...prev.messages, ...row.messages]
      .sort((a, b) => new Date(a.at) - new Date(b.at))
      .slice(-MESSAGES_PER_CHAT);
    if (!prev.lastMessageAt || (row.lastMessageAt && row.lastMessageAt > prev.lastMessageAt)) prev.lastMessageAt = row.lastMessageAt;
  }
  return [...byPhone.values()].sort((a, b) => (b.lastMessageAt || '').localeCompare(a.lastMessageAt || ''));
}

async function upload(lineId, rows) {
  let created = 0, alreadyLeads = 0, filteredByWords = 0, messagesStored = 0;
  for (let i = 0; i < rows.length; i += 200) {
    const res = await fetch(`${API_URL}/api/phone-lines/${lineId}/chatlist`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Reader-Key': READER_KEY },
      body: JSON.stringify({ chats: rows.slice(i, i + 200) }),
    });
    if (!res.ok) throw new Error(`API ${res.status}: ${(await res.text()).slice(0, 200)}`);
    const r = await res.json();
    created += r.created; alreadyLeads += r.alreadyLeads;
    filteredByWords += r.filteredByWords; messagesStored += r.messagesStored;
  }
  return { created, alreadyLeads, filteredByWords, messagesStored, chats: rows.length };
}

async function finish(s) {
  if (s.state === 'uploading' || s.state === 'done') return;
  s.state = 'uploading';
  try {
    const rows = snapshot(s);
    log(`[${s.lineId}] historial completo: ${rows.length} chats con número, subiendo…`);
    s.result = await upload(s.lineId, rows);
    s.state = 'done';
    log(`[${s.lineId}] listo:`, s.result);
  } catch (e) {
    s.state = 'error';
    s.error = e.message;
    log(`[${s.lineId}] falló la subida: ${e.message}`);
    return; // se conserva la sesión para reintentar
  }
  await cleanup(s, { logout: true });
}

async function cleanup(s, { logout }) {
  clearInterval(s.timer);
  try { logout ? await s.sock?.logout() : s.sock?.end(); } catch { /* ya estaba cerrada */ }
  // Las credenciales de un celular ajeno no se guardan más de lo necesario.
  try { fs.rmSync(path.join(DATA_DIR, s.lineId), { recursive: true, force: true }); } catch { /* no-op */ }
}

async function start(lineId) {
  const existing = sessions.get(lineId);
  if (existing && ['qr', 'syncing', 'uploading'].includes(existing.state)) return existing;

  const dir = path.join(DATA_DIR, lineId);
  fs.rmSync(dir, { recursive: true, force: true });
  const { state: auth, saveCreds } = await useMultiFileAuthState(dir);
  const { version } = await fetchLatestBaileysVersion();

  const s = {
    lineId, state: 'starting', qrBase64: null, chats: new Map(), lidToPhone: new Map(),
    batches: 0, progress: null, lastBatchAt: 0, startedAt: new Date().toISOString(),
    error: null, result: null, sock: null, timer: null,
  };
  sessions.set(lineId, s);

  const sock = makeWASocket({
    version,
    auth,
    logger: P({ level: 'silent' }),
    // OJO: con Browsers.macOS('Desktop') WhatsApp corta apenas termina el handshake (428) y
    // nunca llega a emitir el QR. Con este perfil anda.
    browser: Browsers.ubuntu('Chrome'),
    // Que mande TODO el historial que tenga, no sólo lo reciente.
    syncFullHistory: true,
    shouldSyncHistoryMessage: () => true,
    // Sin esto, el celular deja de recibir notificaciones mientras esto esté conectado.
    markOnlineOnConnect: false,
  });
  s.sock = sock;

  sock.ev.on('creds.update', saveCreds);

  sock.ev.on('connection.update', async ({ connection, lastDisconnect, qr }) => {
    if (qr) {
      s.qrBase64 = await QR.toDataURL(qr, { margin: 1, width: 320 });
      s.state = 'qr';
    }
    if (connection === 'open') {
      s.state = 'syncing';
      s.qrBase64 = null;
      s.lastBatchAt = Date.now();
      log(`[${lineId}] conectado, esperando el historial`);
    }
    if (connection === 'close') {
      const code = lastDisconnect?.error?.output?.statusCode;
      if (code === DisconnectReason.loggedOut) {
        if (s.state !== 'done') { s.state = 'error'; s.error = 'La sesión se cerró desde el celular.'; }
        await cleanup(s, { logout: false });
        return;
      }
      // Caída de red mientras sincronizaba: lo que ya llegó no se pierde, se reintenta.
      if (['qr', 'syncing'].includes(s.state)) {
        log(`[${lineId}] conexión caída (${code ?? 's/código'}), reintentando`);
        setTimeout(() => start(lineId).catch((e) => log('retry falló', e.message)), 3000);
      }
    }
  });

  sock.ev.on('lid-mapping.update', ({ lid, pn }) => {
    if (lid && pn) s.lidToPhone.set(userOf(lid), userOf(pn));
  });

  sock.ev.on('messaging-history.set', ({ chats = [], contacts = [], messages = [], progress }) => {
    s.batches++;
    s.lastBatchAt = Date.now();
    if (progress != null) s.progress = progress;
    for (const c of contacts) {
      if (c.lid && c.phoneNumber) s.lidToPhone.set(userOf(c.lid), userOf(c.phoneNumber));
      if (c.id) remember(s, c.id, { name: c.name || c.notify || null, phone: c.phoneNumber ? digits(userOf(c.phoneNumber)) : null });
    }
    for (const c of chats) {
      if (c.id) remember(s, c.id, { name: c.name || null, phone: c.pnJid ? digits(userOf(c.pnJid)) : null });
    }
    for (const m of messages) addMessage(s, m.key?.remoteJid, m);
    log(`[${lineId}] tanda ${s.batches}: ${chats.length} chats, ${contacts.length} contactos, ${messages.length} msgs — total ${s.chats.size}`);
  });

  sock.ev.on('messages.upsert', ({ messages = [] }) => {
    for (const m of messages) {
      const jid = m.key?.remoteJid;
      if (m.key?.senderPn) s.lidToPhone.set(userOf(jid), userOf(m.key.senderPn));
      addMessage(s, jid, m);
    }
  });

  s.timer = setInterval(() => {
    if (s.state === 'syncing' && s.batches > 0 && Date.now() - s.lastBatchAt > IDLE_MS) finish(s);
    if (Date.now() - new Date(s.startedAt).getTime() > SESSION_MAX_MS && ['starting', 'qr'].includes(s.state)) {
      s.state = 'error';
      s.error = 'Nadie escaneó el QR.';
      cleanup(s, { logout: false });
    }
  }, 5000);

  return s;
}

const app = express();
app.use(express.json({ limit: '5mb' }));
app.use((req, res, next) => {
  if (req.path === '/health') return next();
  if (!READER_KEY || req.get('X-Reader-Key') !== READER_KEY) return res.status(401).json({ error: 'unauthorized' });
  next();
});

app.get('/health', (_req, res) => res.json({ status: 'healthy', sessions: sessions.size }));

app.post('/sessions/:lineId', async (req, res) => {
  try { res.json(publicState(await start(req.params.lineId))); }
  catch (e) { log('start falló', e); res.status(500).json({ error: e.message }); }
});

app.get('/sessions/:lineId', (req, res) => {
  const s = sessions.get(req.params.lineId);
  if (!s) return res.status(404).json({ error: 'sin sesión' });
  res.json(publicState(s));
});

/** Cierra el escaneo (lo canceló el usuario o ya terminó): desvincula y borra credenciales. */
app.delete('/sessions/:lineId', async (req, res) => {
  const s = sessions.get(req.params.lineId);
  if (s) { await cleanup(s, { logout: true }); sessions.delete(req.params.lineId); }
  res.json({ ok: true });
});

/** Reintento de la subida cuando la API falló, sin volver a escanear. */
app.post('/sessions/:lineId/retry-upload', async (req, res) => {
  const s = sessions.get(req.params.lineId);
  if (!s) return res.status(404).json({ error: 'sin sesión' });
  s.state = 'syncing';
  await finish(s);
  res.json(publicState(s));
});

app.listen(PORT, () => log(`wa-reader escuchando en ${PORT}`));
