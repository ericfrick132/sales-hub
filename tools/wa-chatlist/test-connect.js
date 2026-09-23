/**
 * Prueba qué combinación (versión de WhatsApp Web + perfil de navegador) deja llegar al QR.
 * WhatsApp cierra la conexión apenas termina el handshake cuando no le gusta el cliente, y el
 * error que devuelve es siempre el mismo (428), así que hay que ir probando.
 */
const P = require('pino');
const { default: makeWASocket, useMultiFileAuthState, fetchLatestBaileysVersion, Browsers } = require('baileys');
const fs = require('fs');

const combos = [
  { name: 'ubuntu/Chrome + version fetch', browser: Browsers.ubuntu('Chrome'), version: null },
  { name: 'macOS/Desktop + version fetch', browser: Browsers.macOS('Desktop'), version: null },
  { name: 'sin browser + version fetch', browser: undefined, version: null },
  { name: 'ubuntu/Chrome + version de Evolution', browser: Browsers.ubuntu('Chrome'), version: [2, 3000, 1048152143] },
];

async function tryOne(combo, fetched) {
  const dir = `/tmp/wa-test-${Date.now()}`;
  const { state, saveCreds } = await useMultiFileAuthState(dir);
  return new Promise((resolve) => {
    const sock = makeWASocket({
      version: combo.version || fetched,
      auth: state,
      logger: P({ level: 'silent' }),
      ...(combo.browser ? { browser: combo.browser } : {}),
      markOnlineOnConnect: false,
    });
    sock.ev.on('creds.update', saveCreds);
    const done = (r) => { try { sock.end(); } catch {} fs.rmSync(dir, { recursive: true, force: true }); resolve(r); };
    const timer = setTimeout(() => done('timeout sin QR'), 15000);
    sock.ev.on('connection.update', ({ qr, lastDisconnect, connection }) => {
      if (qr) { clearTimeout(timer); done('QR OK'); }
      else if (connection === 'close') {
        clearTimeout(timer);
        done(`cerró: ${lastDisconnect?.error?.output?.statusCode ?? '?'} ${lastDisconnect?.error?.message ?? ''}`);
      }
    });
  });
}

(async () => {
  const { version } = await fetchLatestBaileysVersion();
  console.log('version publicada por baileys:', version.join('.'));
  for (const c of combos) {
    process.stdout.write(`${c.name.padEnd(34)} → `);
    console.log(await tryOne(c, version));
  }
  process.exit(0);
})();
