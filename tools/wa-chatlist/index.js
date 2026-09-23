/**
 * Listado COMPLETO de chats de un WhatsApp, con el teléfono real de cada uno.
 *
 * Por qué existe: WhatsApp direcciona los chats por LID (un id opaco que no es un teléfono) y
 * la Evolution del droplet trae baileys 7.0.0-rc.9, que descarta el número al sincronizar el
 * historial — por eso en el CRM entraban chats sin número al que llamar. Desde baileys
 * 7.0.0-rc13 el history sync SÍ expone el teléfono: cada conversación viene con `pnJid` y
 * además llega la tabla `phoneNumberToLidMappings` (ver Utils/history.ts). Este script vincula
 * un dispositivo aparte con esa versión, junta lo que manda WhatsApp y escribe el listado.
 *
 * NO manda mensajes: solo lee. Tampoco se pone "online" (markOnlineOnConnect: false), así las
 * notificaciones le siguen llegando al celular.
 *
 * Uso:
 *   npm install
 *   node index.js                 → escribe chatlist.json (escaneás el QR la primera vez)
 *   node index.js --upload        → además lo sube al CRM como prospectos de remarketing
 *
 * Variables para --upload (o .env):
 *   SALESHUB_URL       default https://api.sales.efcloud.tech
 *   SALESHUB_EMAIL / SALESHUB_PASSWORD
 *   PHONE_LINE_ID      id del teléfono en /devices (de ahí salen los vendedores y el filtro)
 */

const fs = require('fs');
const path = require('path');
const P = require('pino');
const qrcode = require('qrcode-terminal');
const {
  default: makeWASocket,
  useMultiFileAuthState,
  DisconnectReason,
  Browsers,
  fetchLatestBaileysVersion,
} = require('baileys');

const UPLOAD = process.argv.includes('--upload');
const OUT = path.join(__dirname, 'chatlist.json');
const AUTH_DIR = path.join(__dirname, 'auth');
/** Mensajes de contexto que se guardan por chat (los más nuevos). */
const MESSAGES_PER_CHAT = 10;
/** El historial llega en tandas; si pasa este rato sin tandas nuevas, damos por cerrado. */
const IDLE_MS = 90_000;

const log = (...a) => console.log(new Date().toLocaleTimeString('es-AR'), ...a);

/** lid/jid → teléfono (solo dígitos). */
const lidToPhone = new Map();
/** jid del chat → { name, phone, lastAt, messages: [] } */
const chats = new Map();
let lastBatchAt = Date.now();
let batches = 0;
let reconnecting = false;
let ticking = false;

const digits = (s) => (s || '').replace(/\D/g, '');
const userOf = (jid) => (jid || '').split('@')[0].split(':')[0];

/** El teléfono de un jid: directo si ya es un número, o por la tabla de mapeos. */
function phoneFor(jid, explicitPn) {
  if (explicitPn) return digits(userOf(explicitPn));
  if (!jid) return null;
  if (jid.endsWith('@s.whatsapp.net')) return digits(userOf(jid));
  const mapped = lidToPhone.get(userOf(jid));
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

function remember(jid, patch) {
  if (!jid || jid.endsWith('@g.us') || jid === 'status@broadcast' || jid.endsWith('@newsletter')) return null;
  const cur = chats.get(jid) || { jid, name: null, phone: null, lastAt: null, messages: [] };
  Object.assign(cur, Object.fromEntries(Object.entries(patch).filter(([, v]) => v != null && v !== '')));
  chats.set(jid, cur);
  return cur;
}

function addMessage(jid, m) {
  const entry = remember(jid, {});
  if (!entry) return;
  const text = textOf(m);
  if (!text) return;
  const at = Number(m.messageTimestamp) || 0;
  entry.messages.push({ text, fromMe: !!m.key?.fromMe, at, id: m.key?.id || null });
  // Nos quedamos con los más nuevos: para llamar alcanza con saber de qué venían hablando.
  entry.messages.sort((a, b) => b.at - a.at);
  if (entry.messages.length > MESSAGES_PER_CHAT) entry.messages.length = MESSAGES_PER_CHAT;
  if (!entry.lastAt || at > entry.lastAt) entry.lastAt = at;
}

function snapshot() {
  const rows = [];
  for (const c of chats.values()) {
    const phone = phoneFor(c.jid, c.phone ? `${c.phone}@s.whatsapp.net` : null);
    if (!phone || phone.length < 8) continue;          // sin número no hay a quién llamar
    if (!c.messages.length) continue;                   // prospecto = con quien hubo conversación
    rows.push({
      phone,
      name: c.name || null,
      lastMessageAt: c.lastAt ? new Date(c.lastAt * 1000).toISOString() : null,
      messages: c.messages
        .slice()
        .sort((a, b) => a.at - b.at)
        .map((m) => ({ text: m.text, fromMe: m.fromMe, at: new Date(m.at * 1000).toISOString(), id: m.id })),
    });
  }
  // Dedup por teléfono (un mismo contacto puede tener chat por LID y por número).
  const byPhone = new Map();
  for (const r of rows) {
    const prev = byPhone.get(r.phone);
    if (!prev) { byPhone.set(r.phone, r); continue; }
    prev.name = prev.name || r.name;
    const merged = [...prev.messages, ...r.messages].sort((a, b) => new Date(a.at) - new Date(b.at));
    prev.messages = merged.slice(-MESSAGES_PER_CHAT);
    if (!prev.lastMessageAt || (r.lastMessageAt && r.lastMessageAt > prev.lastMessageAt)) prev.lastMessageAt = r.lastMessageAt;
  }
  return [...byPhone.values()].sort((a, b) => (b.lastMessageAt || '').localeCompare(a.lastMessageAt || ''));
}

async function upload(rows) {
  const url = process.env.SALESHUB_URL || 'https://api.sales.efcloud.tech';
  const lineId = process.env.PHONE_LINE_ID;
  if (!lineId) throw new Error('Falta PHONE_LINE_ID (el id del teléfono en /devices)');

  const auth = await fetch(`${url}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: process.env.SALESHUB_EMAIL, password: process.env.SALESHUB_PASSWORD }),
  });
  if (!auth.ok) throw new Error(`Login falló: ${auth.status}`);
  const { token } = await auth.json();

  // De a tandas: son miles de chats y cada uno trae sus mensajes.
  let created = 0, existing = 0, filtered = 0, stored = 0;
  for (let i = 0; i < rows.length; i += 200) {
    const slice = rows.slice(i, i + 200);
    const res = await fetch(`${url}/api/phone-lines/${lineId}/chatlist`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ chats: slice }),
    });
    if (!res.ok) throw new Error(`Subida falló (${res.status}): ${(await res.text()).slice(0, 300)}`);
    const r = await res.json();
    created += r.created; existing += r.alreadyLeads; filtered += r.filteredByWords; stored += r.messagesStored;
    log(`subidos ${Math.min(i + 200, rows.length)}/${rows.length} — ${created} prospectos nuevos`);
  }
  log(`LISTO: ${created} prospectos nuevos, ${existing} ya eran leads, ${filtered} fuera por el filtro de palabras, ${stored} mensajes guardados`);
}

async function finish() {
  const rows = snapshot();
  fs.writeFileSync(OUT, JSON.stringify(rows, null, 2));
  const conNombre = rows.filter((r) => r.name).length;
  log(`Listado escrito en ${OUT}: ${rows.length} chats con número (${conNombre} con nombre)`);
  if (UPLOAD) {
    try { await upload(rows); }
    catch (e) { console.error('No se pudo subir:', e.message); process.exitCode = 1; }
  }
  process.exit(process.exitCode || 0);
}

async function main() {
  const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
  // Sin la versión actual de WhatsApp Web el server corta la conexión al toque (428) y ni
  // siquiera llega a emitir el QR.
  // WA_VERSION permite fijar la que se sabe que funciona (la que reporta Evolution en el
  // droplet), porque la que publica baileys a veces viene atrasada y el server rechaza.
  const version = process.env.WA_VERSION
    ? process.env.WA_VERSION.split('.').map(Number)
    : (await fetchLatestBaileysVersion()).version;
  log(`WhatsApp Web v${version.join('.')}`);
  const sock = makeWASocket({
    version,
    auth: state,
    logger: P({ level: process.env.WA_DEBUG ? 'trace' : 'silent' }),
    // OJO: con Browsers.macOS('Desktop') WhatsApp corta la conexión apenas termina el
    // handshake (428) y nunca emite el QR. Con este perfil anda (ver test-connect.js).
    browser: Browsers.ubuntu('Chrome'),
    // Que WhatsApp mande TODO el historial que tenga, no solo lo reciente.
    syncFullHistory: true,
    shouldSyncHistoryMessage: () => true,
    // Sin esto el celular deja de recibir notificaciones mientras este cliente esté conectado.
    markOnlineOnConnect: false,
  });

  sock.ev.on('creds.update', saveCreds);

  sock.ev.on('connection.update', ({ connection, lastDisconnect, qr }) => {
    if (qr) {
      console.log('\nEscaneá este QR desde el celular: WhatsApp → Ajustes → Dispositivos vinculados → Vincular un dispositivo\n');
      qrcode.generate(qr, { small: true });
    }
    if (connection === 'open') log('Conectado. Esperando el historial (llega en tandas)…');
    if (connection === 'close') {
      const err = lastDisconnect?.error;
      const code = err?.output?.statusCode;
      if (code === DisconnectReason.loggedOut) {
        console.error('La sesión se cerró desde el celular. Borrá la carpeta auth/ y volvé a escanear.');
        process.exit(1);
      }
      log(`Conexión caída (${code ?? 's/código'}: ${err?.message ?? 'sin detalle'}), reconectando…`);
      // Una reconexión por vez: sin esto cada caída abre otro socket y se apilan.
      if (reconnecting) return;
      reconnecting = true;
      setTimeout(() => { reconnecting = false; main().catch((e) => { console.error(e); process.exit(1); }); }, 3000);
    }
  });

  // La tabla LID↔teléfono que manda WhatsApp (y la que baileys va aprendiendo).
  sock.ev.on('lid-mapping.update', ({ lid, pn }) => {
    if (lid && pn) lidToPhone.set(userOf(lid), userOf(pn));
  });

  sock.ev.on('messaging-history.set', ({ chats: cs = [], contacts = [], messages = [], isLatest, progress }) => {
    batches++;
    lastBatchAt = Date.now();
    for (const c of contacts) {
      if (c.lid && c.phoneNumber) lidToPhone.set(userOf(c.lid), userOf(c.phoneNumber));
      if (c.id) {
        remember(c.id, {
          name: c.name || c.notify || null,
          phone: c.phoneNumber ? digits(userOf(c.phoneNumber)) : null,
        });
      }
    }
    for (const c of cs) {
      if (c.id) remember(c.id, { name: c.name || null, phone: c.pnJid ? digits(userOf(c.pnJid)) : null });
    }
    for (const m of messages) addMessage(m.key?.remoteJid, m);
    log(`tanda ${batches}: ${cs.length} chats, ${contacts.length} contactos, ${messages.length} mensajes` +
        `${progress != null ? ` (${progress}%)` : ''} — acumulado: ${chats.size} chats, ${lidToPhone.size} números resueltos`);
    if (isLatest) log('WhatsApp dice que ya mandó todo lo que tenía.');
  });

  // Mensajes que llegan mientras corre (también traen el número real).
  sock.ev.on('messages.upsert', ({ messages = [] }) => {
    for (const m of messages) {
      const jid = m.key?.remoteJid;
      if (m.key?.senderPn) lidToPhone.set(userOf(jid), userOf(m.key.senderPn));
      addMessage(jid, m);
    }
  });

  if (!ticking) {
    ticking = true;
    setInterval(() => {
      if (batches > 0 && Date.now() - lastBatchAt > IDLE_MS) {
        log(`Sin tandas nuevas hace ${Math.round(IDLE_MS / 1000)}s: cierro.`);
        finish();
      }
    }, 5000);
    process.on('SIGINT', finish);
  }
}

main().catch((e) => { console.error(e); process.exit(1); });
