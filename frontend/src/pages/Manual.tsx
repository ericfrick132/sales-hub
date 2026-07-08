import { ReactNode, useEffect, useMemo, useState } from 'react';

/**
 * Manual de usuario del sistema: qué hace cada pantalla, cómo se configura
 * (checklists con tildes verdes persistidos en localStorage) y los comandos
 * por WhatsApp. Es contenido estático generado del relevamiento del código;
 * si el sistema cambia, regenerarlo.
 */

const LS_KEY = 'saleshub-manual-v1';

// ---------- helpers de contenido ----------
const Cmd = ({ children }: { children: ReactNode }) => (
  <code className="font-mono text-[12px] bg-emerald-50 text-emerald-800 rounded px-1.5 py-0.5">{children}</code>
);

type Check = { id: string; label: ReactNode };
type CardDef = {
  title: string;
  routes?: string[];
  admin?: boolean;
  desc: ReactNode;
  actions?: ReactNode[]; // bullets colapsables
  checks?: Check[];
  note?: ReactNode; // gotcha ámbar
  tip?: ReactNode;  // tip azul
};
type SectionDef = { id: string; eyebrow: string; title: string; intro?: ReactNode; cards: CardDef[] };

const SECTIONS: SectionDef[] = [
  {
    id: 'puesta-a-punto',
    eyebrow: 'Checklist maestro',
    title: 'Puesta a punto: el circuito mínimo para vender',
    intro: 'En este orden. Si todos estos tildes están en verde, la máquina vende sola.',
    cards: [
      {
        title: 'Checklist',
        desc: null,
        checks: [
          { id: 'setup-producto', label: <>Producto activo en <Cmd>/products</Cmd> con cadencia cargada (pasos texto/audio/imagen, no el template legacy).</> },
          { id: 'setup-playbook', label: <>Playbook de IA escrito en el producto (cómo vende el bot) + precio display + checkout URL.</> },
          { id: 'setup-vendedor', label: <>Vendedor creado en <Cmd>/sellers</Cmd> con whitelist de productos y zonas asignadas (o regiones catch-all).</> },
          { id: 'setup-wa', label: <>WhatsApp del vendedor conectado (<Cmd>/connect</Cmd>, QR) y switch de envío ON.</> },
          { id: 'setup-gauges', label: <>Velocidad de envío revisada: número nuevo = preset Cauteloso unos días (anti-ban).</> },
          { id: 'setup-piloto', label: <>Piloto automático ON en el producto (el bot responde solo) y, si querés, re-enganche.</> },
          { id: 'setup-flags', label: <>Runners prendidos en el Centro de control: mínimo <Cmd>whatsapp</Cmd>; según lo que uses, <Cmd>captacion</Cmd>, <Cmd>onboarding</Cmd>, <Cmd>posteos</Cmd>, <Cmd>seo</Cmd>, <Cmd>instagram</Cmd>, <Cmd>reengage</Cmd>.</> },
          { id: 'setup-leads', label: <>Leads entrando: pipeline corriendo, o importación, o anuncios apuntando a la línea.</> },
          { id: 'setup-objetivos', label: <>Objetivos cargados en <Cmd>/objetivos</Cmd> (sin metas no hay barras de progreso ni cumplimiento).</> },
        ],
      },
    ],
  },
  {
    id: 'admin',
    eyebrow: 'Tablero central',
    title: 'Panel admin y flags',
    cards: [
      {
        title: 'Hoy',
        routes: ['/admin'],
        admin: true,
        desc: 'Panel de comando: te dice si la máquina está vendiendo y concentra todos los interruptores que la prenden.',
        actions: [
          <>Centro de control: badge "Vendiendo / No está vendiendo" con 3 chequeos guiados; switches de runners (los 7 flags), de envío por vendedor y de piloto / re-enganche por app. Los switches de acá son los que prenden la operación de verdad.</>,
          <>Leads de anuncios por app (Meta forms + WhatsApp ads, con drill-down a /leads filtrado).</>,
          <>Cumplimiento hoy (metas por vendedor) · Costo IA (gasto real por feature) · Efectividad por app (embudo con filtros de origen y período).</>,
          <>Tareas de hoy (posts/runners/seo del día) · Calendario · Actividad por vendedor (heatmap 14 días) · Estado de envíos por instancia.</>,
        ],
        checks: [
          { id: 'admin-control', label: <>Recorrí el Centro de control y dejaste el badge en "Vendiendo".</> },
          { id: 'admin-costo', label: <>El widget Costo IA muestra números (si da $0 constante, la key de Anthropic en prod está vencida).</> },
        ],
        note: <><b>Los 7 runners (flags):</b> <Cmd>whatsapp</Cmd> envíos+respuestas IA · <Cmd>onboarding</Cmd> alta por ads · <Cmd>instagram</Cmd> DMs+follow+scraping (5 workers de una) · <Cmd>posteos</Cmd> · <Cmd>captacion</Cmd> · <Cmd>seo</Cmd> · <Cmd>reengage</Cmd> import de leads B2B (GymHero/TurnosPro) · <Cmd>voicenote</Cmd> notas de voz IA con la voz clonada. Se togglean sin reiniciar. Ojo: <Cmd>onboarding</Cmd> arranca apagado por defecto; los demás, prendidos salvo que los apagues.</>,
      },
    ],
  },
  {
    id: 'captacion',
    eyebrow: 'De dónde salen los leads',
    title: 'Captación de leads',
    cards: [
      {
        title: 'Pipeline / Captación',
        routes: ['/pipeline'],
        admin: true,
        desc: 'Dispara y monitorea corridas de scraping: Places API, Google Maps (Apify), Instagram, Meta Ads Library y Facebook Posts.',
        actions: [
          <>Trigger corrida: producto + fuente + modo (Auto / Elegir ciudad / Próximas N en cola) + filtro por tamaño de ciudad + categoría + tope de leads + check "Auto-encolar mensajes".</>,
          <>Importar ciudades AR: catálogo GeoNames gratis (lo necesita el dropdown de ciudades y el mapa de zonas).</>,
          <>Tabla de corridas con leads creados y errores (refresh 15s). Modo batch corre N ciudades con pausa de 45s.</>,
        ],
        checks: [
          { id: 'pipe-places', label: <>API key de Google Places cargada (entra en el free tier de $200/mes).</> },
          { id: 'pipe-apify', label: <>Token de Apify cargado (Maps/IG/Meta Ads/FB; cap diario de runs para cuidar los $5/mes).</> },
          { id: 'pipe-ciudades', label: <>Catálogo de ciudades AR importado.</> },
          { id: 'pipe-flag', label: <>Flag <Cmd>captacion</Cmd> prendido si querés corridas automáticas programadas.</> },
        ],
      },
      {
        title: 'Importar de Google Maps',
        routes: ['/leads/import'],
        desc: 'Pegás el listado crudo de una búsqueda de Maps y parsea nombre, dirección, teléfono y rating.',
        actions: [
          <>Producto + origen + estado inicial + ciudad; check "Enriquecer con Google Places" (~$0.04/lead: completa teléfono, web y coordenadas — recomendado, sin esto solo entran los que ya traen teléfono).</>,
          <>Admin puede destildar "Asignarme estos leads" para mandarlos al Pool. Resultado: detectados / insertados / duplicados / cerrados.</>,
        ],
      },
      {
        title: 'Buscar leads (captura con extensión)',
        routes: ['/leads/search'],
        desc: 'Te sugiere qué zona/categoría capturar en Google Maps y sube los negocios con un script de Tampermonkey mientras navegás.',
        checks: [
          { id: 'cap-tamper', label: <>Instalar Tampermonkey + el script <Cmd>saleshub-capture.user.js</Cmd> (guía en la misma página).</> },
          { id: 'cap-token', label: <>Copiar token desde la página y pegarlo en el panel SalesHub dentro de Google Maps + elegir producto.</> },
          { id: 'cap-login', label: <>Estar logueado en Google Maps (sin login no se ven teléfonos) y tener zonas asignadas.</> },
        ],
        note: <>En Maps hay que clickear cada negocio y usar <b>"+ Este lugar"</b>. La página muestra "próxima captura" con la mejor zona pendiente y alternativas.</>,
      },
      {
        title: 'Mapas de zonas',
        routes: ['/map', '/sellers/zones'],
        admin: true,
        desc: 'Dos mapas distintos que alimentan el reparto automático: /map por localidad (multi-vendedor con round-robin; Alt+click selecciona provincia entera) y /sellers/zones por ciudad/provincia (círculos, asignación por provincia completa).',
        checks: [
          { id: 'map-polys', label: <>Polígonos de localidades cargados (si /map lo pide: <Cmd>scripts/localities/build.mjs</Cmd> + <Cmd>import.mjs</Cmd>).</> },
          { id: 'map-zonas', label: <>Cada vendedor tiene zonas asignadas (gris = sin dueño → Pool).</> },
        ],
      },
      {
        title: 'Pool de leads',
        routes: ['/pool'],
        desc: 'Bolsa de leads sin asignar: cualquier vendedor los puede Tomar. El admin además tiene "Reasignar todos" en la pestaña "Sin asignar" de /leads (solo reparte a vendedores conectados que matcheen whitelist/región).',
      },
    ],
  },
  {
    id: 'ventas',
    eyebrow: 'El día a día',
    title: 'Ventas y conversaciones',
    cards: [
      {
        title: 'Aplicaciones (productos y cadencias)',
        routes: ['/products'],
        admin: true,
        desc: 'El corazón de la configuración: cada app que vendés, su cadencia de mensajes, el playbook del bot y los switches de piloto automático y re-enganche.',
        actions: [
          <>CadencesEditor: pasos 💬 texto / 🎤 audio / 🖼️ imagen / 📄 PDF con delays, variantes de audio (round-robin A/B) y overrides por categoría. Placeholders: <Cmd>{'{name}'}</Cmd> <Cmd>{'{city}'}</Cmd> <Cmd>{'{category}'}</Cmd> <Cmd>{'{checkout_url}'}</Cmd> y spin-text <Cmd>{'{Hola!|Buenas!}'}</Cmd>.</>,
          <>Reglas/Playbook de la IA en lenguaje natural + precio display + checkout URL + horarios de envío.</>,
          <>Respuestas rápidas por producto: <Cmd>/clave = contenido</Cmd> (aparecen como botones y slash-commands en Conversaciones).</>,
          <>Probar envío: dispara la cadencia real a un número tuyo antes de lanzarla.</>,
          <>Línea de WhatsApp propia por app (badge 📱): distinta del WhatsApp del vendedor.</>,
        ],
        checks: [
          { id: 'prod-cadencia', label: <>Cadencia con pasos cargada (sin pasos cae al mensaje legacy) — guardá el producto antes de adjuntar archivos.</> },
          { id: 'prod-variantes', label: <>Audios con varias variantes (anti-detección de Meta) — medí cuál rinde en /audio-analytics.</> },
          { id: 'prod-piloto', label: <>🤖 Piloto automático ON (el re-enganche requiere piloto prendido primero).</> },
          { id: 'prod-rapidas', label: <>Respuestas rápidas cargadas (ahorran IA y tiempo en Conversaciones).</> },
          { id: 'prod-test', label: <>Cadencia probada con "Probar envío" contra tu número.</> },
        ],
      },
      {
        title: 'Conectar WhatsApp',
        routes: ['/connect'],
        desc: 'Vincular el WhatsApp del vendedor por QR, prender el envío, ajustar la velocidad humanizada y ver la cola con hora estimada (ETA).',
        actions: [
          <>QR (Configuración → Dispositivos vinculados → Vincular dispositivo) · switch Envío automático · Desvincular.</>,
          <>Velocidad de envío: presets Cauteloso / Equilibrado / Agresivo o sliders finos (cap diario, warmup, horario, pausas, tandas, "escribiendo…", día libre, leer entrantes primero, zona horaria).</>,
          <>Mi cola de envío con ETA real (considera cap, ventana horaria, warmup y jitter; vacía = envío pausado).</>,
        ],
        note: <>Número recién conectado → preset <b>Cauteloso</b> unos días. No se puede prender envío sin WhatsApp conectado.</>,
      },
      {
        title: 'Conversaciones',
        routes: ['/conversations'],
        desc: 'Bandeja tipo WhatsApp de los leads que respondieron: sugerencias de IA, respuestas rápidas y el botón de takeover del bot.',
        actions: [
          <>Filtros por producto / vendedor (admin) / estado / período.</>,
          <>🤖 activo/pausado por conversación = takeover humano (igual que mandar <Cmd>-</Cmd> / <Cmd>+</Cmd> desde el celu).</>,
          <>💡 Sugerencia IA con Usar / Descartar · respuestas rápidas con slash-commands (<Cmd>/precio</Cmd> + espacio o Tab expande).</>,
        ],
        tip: <>Si escribís a mano desde el celular en el chat de un lead, el bot se pausa solo para ese lead. Para devolvérselo al bot: mandá <Cmd>+</Cmd> en ese chat o usá el toggle acá.</>,
      },
      {
        title: 'Mis leads / Leads del equipo · Detalle',
        routes: ['/leads', '/leads/:id'],
        desc: 'Listado con filtros (estado, producto, origen — incluye "Anuncios Meta + WhatsApp") y alta manual con detector de duplicados. El detalle muestra la cadencia que le va a salir, cambia estado con notas, asigna vendedor y encola.',
        actions: [
          <>Encolar envío automático (requiere vendedor asignado + WhatsApp del lead — si falta algo lo avisa en amarillo).</>,
          <>Enriquecer con Instagram / desde website (completan datos del negocio).</>,
          <>Alta manual: check "Encolar cadencia automática" + "Guardar y cargar otro" para carga en serie.</>,
        ],
      },
      {
        title: 'Vendedores · Detalle',
        routes: ['/sellers', '/admin/sellers/:id'],
        admin: true,
        desc: 'ABM de vendedores: whitelist de productos, regiones, gauges de humanización por vendedor, reset de password, y reglas keyword→respuesta. El detalle es el reporte individual (funnel, actividad 30 días, conversaciones).',
        checks: [
          { id: 'sell-whitelist', label: <>Whitelist de productos + regiones (vacío = catch-all; ciudad gana sobre provincia).</> },
          { id: 'sell-keywords', label: <>Reglas keyword→respuesta cargadas (una por línea, <Cmd>keyword = respuesta</Cmd>): contestan sin gastar IA.</> },
        ],
      },
      {
        title: 'Objetivos · Performance de audios',
        routes: ['/objetivos', '/audio-analytics'],
        admin: true,
        desc: <>Objetivos: metas diarias/semanales por vendedor y app (Contactos / Respuestas / Demos / Cierres) que alimentan las barras del dashboard y el widget de cumplimiento. Audio-analytics: compara reply-rate por cadencia y por archivo de audio (🏆 al mejor con mínimo 5 envíos) — usalo para decidir qué variante de audio dejar.</>,
      },
    ],
  },
  {
    id: 'bot',
    eyebrow: 'La máquina de responder',
    title: 'Bot IA y onboarding',
    cards: [
      {
        title: 'Reglas de la IA',
        routes: ['/reglas-ia'],
        admin: true,
        desc: 'Reglas duras en lenguaje natural que el bot DEBE cumplir en toda respuesta, por encima del playbook de cada app. Ej: "cuando confirme que quiere comprar, mandale el link de checkout y nada más".',
        checks: [{ id: 'ia-reglas', label: <>Reglas globales revisadas y activas (checkbox "Activa" por regla; se aplican a todas las apps).</> }],
      },
      {
        title: 'Notas de voz IA (voz clonada)',
        routes: ['/voice-test'],
        admin: true,
        desc: 'El bot responde con nota de voz (voz clonada de Eric, ElevenLabs) en momentos decisivos, con azar para no ser mecánico: espejo (el lead mandó audio, 80%), decisivo (precio/planes/cuenta/demo, 35%) y despedida (cierra con "un abrazo", 60%). El guion se reescribe con la guía de estilo de Eric (muletillas reales, voseo, "shama", lucas).',
        actions: [
          <>Flag <Cmd>voicenote</Cmd> en el Centro de control (runners) — prendido/apagado sin reiniciar.</>,
          <>Límites: 1 audio por lead cada 24 h, tope global 20/día (créditos ElevenLabs). Si el audio falla, sale el texto normal.</>,
          <>/voice-test: escribís un texto + número y sale como audio con la voz — LITERAL (respeta palabra por palabra, solo adapta puntuación y tono).</>,
          <>Calibración por WhatsApp: mandá <Cmd>calibrar</Cmd> al chat de la línea (desde un número autorizado) y el bot te guía guion por guion para grabar tomas que mejoran el clon.</>,
        ],
        checks: [
          { id: 'vn-flag', label: <>Flag <Cmd>voicenote</Cmd> ON y voz elegida (hoy: Eric Frick v4).</> },
          { id: 'vn-test', label: <>Probaste cómo suena desde <Cmd>/voice-test</Cmd> a tu número.</> },
          { id: 'vn-calibrar', label: <>Tomas de calibración grabadas (mandá "calibrar" — acumula para el Professional Voice Clone).</> },
        ],
      },
      {
        title: 'Onboarding de apps',
        routes: ['/onboarding-apps'],
        admin: true,
        desc: 'El bot de alta para leads de anuncios: preguntas guiadas por app y al final crea la cuenta solo (autoservicio) o deriva a demo (venta asistida).',
        actions: [
          <>Por app: Activo/Inactivo · modo Autoservicio / Venta asistida · intro · preguntas (la 1ª siempre es el nombre del negocio) · pedido de mail · endpoint de provisión (bot-register) · mensaje de éxito con <Cmd>{'{accessUrl}'}</Cmd>.</>,
          <>Espera antes de responder (random entre min y max — más humano).</>,
          <>🎙 Audio del pitch: grabá varias tomas desde el navegador; rota al azar entre variantes (anti-detección).</>,
          <><Cmd>[NUEVO_MENSAJE]</Cmd> divide un texto en varios mensajes.</>,
        ],
        checks: [
          { id: 'onb-flag', label: <>Flag <Cmd>onboarding</Cmd> prendido (arranca apagado por defecto — sin esto ninguna app onboardea).</> },
          { id: 'onb-apps', label: <>Cada app que corre ads tiene su card "Activo" con provisión configurada y probada.</> },
          { id: 'onb-audio', label: <>Varias tomas de audio grabadas para el pitch.</> },
        ],
        note: <>El disparo: un número desconocido escribe el texto del anuncio (contiene "activar / quiero / me interesa…" + el nombre de la app) → se crea el lead WhatsAppAd y arranca el flujo. Si no menciona la app, no se crea nada.</>,
      },
      {
        title: 'Seguimientos (follow-up de abandono)',
        routes: ['/seguimientos'],
        admin: true,
        desc: 'Config central del follow-up de abandono POR APP (ej. abandonó el OTP): las apps bajan las secuencias y las ejecutan con sus canales; acá ves cobertura y reportes de conversión.',
        checks: [
          { id: 'seg-secuencias', label: <>Secuencias creadas (app + trigger, ej. <Cmd>otp_abandoned</Cmd>) con pasos y switch "Activa".</> },
          { id: 'seg-backlog', label: <>Mensajes de backlog (tibio/frío) definidos para abandonos viejos.</> },
          { id: 'seg-todo', label: <>Apps con badge "TODO" conectadas al push de leads (hoy cubren gymhero y turnos-pro; falta el resto).</> },
        ],
      },
      {
        title: 'Transcripción de audios',
        routes: ['/transcripcion'],
        admin: true,
        desc: 'Relay personal: reenviás una nota de voz a la línea elegida y te devuelve el texto (Whisper/Groq). Con modo batch junta imágenes+textos+audios y devuelve UN PDF de requerimientos.',
        checks: [
          { id: 'tr-modulo', label: <>Switch "Módulo activo" ON.</> },
          { id: 'tr-linea', label: <>Línea de WhatsApp elegida (sin línea, queda apagado fail-safe).</> },
          { id: 'tr-numeros', label: <>Números autorizados cargados (allowlist; matchea por últimos 8 dígitos).</> },
          { id: 'tr-batch', label: <>Si querés PDFs: modo batch ON + ventana entre mensajes (default 10 s).</> },
        ],
      },
    ],
  },
  {
    id: 'social',
    eyebrow: 'Fábrica de contenido',
    title: 'Contenido social',
    cards: [
      {
        title: 'Posteos',
        routes: ['/posteos'],
        admin: true,
        desc: 'Fábrica de contenido por app y por red: concepto + caption + imagen/video con IA, logo real compositado, y distribución a Buffer (API) o cola Warmr (device real).',
        actions: [
          <>⚡ Publicar YA: elegís cuentas + tema opcional y sube al toque.</>,
          <>Por app: marca (colores/tono/pilares) · logo (subir o auto de la landing) · landing como fuente de verdad ("Actualizar ficha" lee features/precios reales; se relee cada 14 días) · frecuencia/horarios.</>,
          <>Por red: formato (Story/Reel/Post/Carousel), imagen|video, slides (carrusel/combo stories), distribución Buffer o Warmr, 📲 Notify Me (push al teléfono para publicar nativo — ideal Reels con audio trending), prompt propio, "Generar ahora".</>,
        ],
        checks: [
          { id: 'post-claude', label: <>Claude API key (texto) — la misma del bot.</> },
          { id: 'post-imagen', label: <>Imagen IA: proveedor configurado (openai/grok/gemini, o pollinations gratis sin key).</> },
          { id: 'post-fal', label: <>Video IA: key de fal.ai + modelo (Seedance/Kling/Veo).</> },
          { id: 'post-buffer', label: <>Buffer token + canales conectados en Buffer; cada red con cuenta destino elegida.</> },
          { id: 'post-narracion', label: <>Narración en video (opcional): ElevenLabs configurado — ya hay key Starter cargada.</> },
          { id: 'post-flag', label: <>Para que postee solo: app activa + red activa + destino + flag <Cmd>posteos</Cmd> ON.</> },
          { id: 'post-notify', label: <>Notify Me: app móvil de Buffer instalada con notificaciones + checkbox prendido en los canales de video.</> },
        ],
      },
      {
        title: 'Calendario · Cola Warmr',
        routes: ['/calendario', '/warmr'],
        desc: 'Calendario: todos los posteos de todas las apps en vista mes/semana; drag & drop para reprogramar, backlog "sin agendar", modal para editar caption/hashtags, regenerar imagen y "Programar en Buffer" (requiere asset generado). Cola Warmr: paso manual para video corto — descargás el clip, copiás el caption y lo subís a app.warmr.so → Cloud Drop; después "✓ Marcar como subido".',
        tip: <>Regla de la casa: <b>imagen → Buffer, video → Warmr</b> (device real, sin el handicap del audio trending de la API).</>,
      },
      {
        title: 'Inspiración · Competencia · Tendencias',
        routes: ['/inspiracion', '/competitors', '/trends'],
        desc: 'Circuito de referencia: seguís cuentas de la competencia (Apify), curás con ❤️ lo que te gusta, y "Generar inspirado" crea tu versión adaptada a la marca. Competencia además muestra comentarios negativos (leads calientes para robarle a la competencia). Tendencias deriva top hashtags/posts de esa data + scrape de TikTok.',
        checks: [
          { id: 'insp-competidores', label: <>Competidores cargados por vertical (sin esto, Tendencias queda vacía).</> },
          { id: 'insp-wa', label: <>Entrada por WhatsApp de ideas activada (línea + número maestro) — ver comandos <Cmd>insp</Cmd>/<Cmd>tema</Cmd>.</> },
        ],
      },
      {
        title: 'SEO / Contenido',
        routes: ['/seo'],
        admin: true,
        desc: 'Motor autónomo de SEO: investiga keywords reales (Google via Apify), genera artículos con FAQ + JSON-LD y los publica en el /blog de cada app por commit a GitHub.',
        checks: [
          { id: 'seo-sitios', label: <>Sitios creados (botón "Crear las 6 apps") con nicho/audiencia/voz de marca completos.</> },
          { id: 'seo-github', label: <>GitHub token + repo/branch por sitio para auto-publicar por commit.</> },
          { id: 'seo-auto', label: <>Toggle "Publicación automática" por sitio + flag <Cmd>seo</Cmd> ON (sin ambos, todo queda "Para revisar").</> },
        ],
      },
    ],
  },
  {
    id: 'instagram',
    eyebrow: 'Growth por navegador',
    title: 'Instagram growth',
    cards: [
      {
        title: 'Cuentas IG',
        routes: ['/instagram/accounts'],
        admin: true,
        desc: 'Las cuentas de Instagram que mandan DMs, hacen auto-follow y scrapean (automatización por navegador con pacing anti-ban: 20 DM/hora, 40/día).',
        checks: [
          { id: 'ig-key', label: <><Cmd>SALESHUB_INSTAGRAM_ENCRYPTION_KEY</Cmd> seteada (cifra los passwords).</> },
          { id: 'ig-proxy', label: <>Proxy residencial fijo por cuenta (iProxy/IPRoyal, 1 IP por identidad — sin proxy IG pide verificación).</> },
          { id: 'ig-2fa', label: <>2FA secret (TOTP, Base32) cargado por cuenta para login automático.</> },
          { id: 'ig-flag', label: <>Flag <Cmd>instagram</Cmd> ON (prende los 5 workers de IG de una).</> },
        ],
      },
      {
        title: 'Auto-follow IG',
        routes: ['/instagram/follow'],
        admin: true,
        desc: 'Campañas que siguen a los seguidores de un perfil ajeno a ritmo configurable (para captar audiencia). Scrapea con la propia sesión de la cuenta, no gasta Apify.',
        checks: [
          { id: 'igf-campana', label: <>Campaña creada: cuenta ejecutora + perfil source + follows/día (&lt;50/día recomendado) + horario AR.</> },
        ],
        note: <>Si el source es privado o IG limita su lista, el scrape devuelve 0 (lo avisa con ⚠).</>,
      },
    ],
  },
];

// Comandos por WhatsApp (sección especial con tablas)
const WA_TAKEOVER: Array<{ cmd: string; desc: ReactNode }> = [
  { cmd: '-', desc: 'Pausa el bot para ese lead (deja de responder y de re-enganchar).' },
  { cmd: '+', desc: 'Reactiva el bot para ese lead.' },
  { cmd: 'cualquier mensaje manual', desc: 'Takeover implícito: se registra en la conversación y el bot se pausa solo para ese lead.' },
];
const WA_MASTER: Array<{ cmd: string; desc: ReactNode }> = [
  { cmd: 'insp / ideas', desc: 'Abre modo INSPIRACIÓN 30 min: todo lo que mandes (imágenes/ideas/links) se guarda para posteos. Al cerrar te pregunta el tema (menú numerado o texto libre, ej. "gymhero motivación").' },
  { cmd: 'pdf / req', desc: 'Abre modo PDF: mandá imágenes/texto/audios y al frenar unos segundos devuelve UN PDF de requerimientos.' },
  { cmd: 'tema ...', desc: <>Fija el tema del día (tiñe los posteos de hoy de todas las apps). <Cmd>tema off</Cmd> lo borra.</> },
  { cmd: 'listo', desc: <>Cierra la sesión activa. <Cmd>cancelar</Cmd> descarta lo pendiente.</> },
  { cmd: 'imagen/idea suelta', desc: 'Sin sesión abierta, el bot pregunta: 1 = inspiración, 2 = PDF.' },
  { cmd: 'nota de voz', desc: 'Si el relay de transcripción está activo y tu número está en la allowlist: te devuelve el texto. Audio a vos mismo (self-chat) también se transcribe.' },
  { cmd: 'calibrar', desc: 'Sesión de calibración de voz: el bot te tira guiones de a uno y grabás cada uno como nota de voz. "repetir" regraba, "saltar" saltea, "listo" corta. Acumulativo (arranca donde dejaste).' },
];

// Estado "apagado / sin usar" (foto al 7 jul 2026)
const OFF_TABLE: Array<{ f: ReactNode; estado: string; on?: boolean; falta: ReactNode }> = [
  { f: <b>Onboarding por ads (bot de alta)</b>, estado: 'LIVE (flag ON)', on: true, falta: <>Ya opera con soporte real: si el lead no puede entrar, regenera el acceso contra la app.</> },
  { f: <b>Transcripción de audios (+ batch PDF)</b>, estado: 'deployado, OFF', falta: <>Switch en /transcripcion + línea + allowlist.</> },
  { f: <b>Pipeline social (posteos automáticos)</b>, estado: 'LIVE (flag ON)', on: true, falta: <>7 apps publican auto a Buffer; falta conectar los TikToks y logos de turnospro/archicloud.</> },
  { f: <b>Notify Me (Reels nativos vía Buffer)</b>, estado: 'a medias', falta: <>App móvil de Buffer + prender el checkbox en los canales de video.</> },
  { f: <b>Auto-follow IG</b>, estado: 'esperando proxies', falta: <>Comprar proxies fijos (iProxy/IPRoyal) y prender flag <Cmd>instagram</Cmd>.</> },
  { f: <b>Follow-up unificado (abandono por app)</b>, estado: 'construido, sin deployar', falta: <>Deploy + conectar gymhero (turnos-pro ya está).</> },
  { f: <b>Big-CRM Hub (leads B2B de las apps)</b>, estado: 'construido, sin configurar', falta: <>Config en los 3 repos + flag <Cmd>reengage</Cmd>.</> },
  { f: <b>Notas de voz IA con tu voz (ElevenLabs)</b>, estado: 'LIVE (flag ON)', on: true, falta: <>El bot responde en audio en momentos decisivos; seguir calibrando con "calibrar".</> },
  { f: <b>Generador de imágenes (Pollinations)</b>, estado: 'página huérfana', falta: <>Existe el componente pero no está ruteado en el menú.</> },
  { f: <b>Landings WhatsApp+OTP (archicloud/playcrew)</b>, estado: 'LIVE', on: true, falta: <>Apuntar las campañas de Meta a las landings y despausarlas.</> },
  { f: <b>Meta Lead Ads (webhook forms)</b>, estado: 'verificado e2e', on: true, falta: <>Lanzar campañas objetivo Leads. Ojo: el PageAccessToken vence ~28 ago 2026.</> },
];

const ALL_IDS = SECTIONS.flatMap((s) => s.cards.flatMap((c) => (c.checks ?? []).map((k) => k.id)));

// ---------- componentes ----------
function CheckRow({ item, checked, onToggle }: { item: Check; checked: boolean; onToggle: () => void }) {
  return (
    <label className="flex items-start gap-2.5 rounded-lg px-2 py-1.5 cursor-pointer hover:bg-emerald-50">
      <input
        type="checkbox"
        checked={checked}
        onChange={onToggle}
        className="mt-0.5 h-[18px] w-[18px] flex-none rounded border-2 border-emerald-500 text-emerald-600 focus:ring-emerald-500 accent-emerald-600"
      />
      <span className={checked ? 'text-slate-400 line-through decoration-emerald-400' : 'text-slate-700'}>{item.label}</span>
    </label>
  );
}

function CmdTable({ rows }: { rows: Array<{ cmd: string; desc: ReactNode }> }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="text-left text-[10px] uppercase tracking-wider text-slate-400 border-b">
            <th className="py-1.5 pr-3">Mandás</th>
            <th className="py-1.5">Qué hace</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.cmd} className="border-b border-slate-100 align-top">
              <td className="py-2 pr-3 whitespace-nowrap"><Cmd>{r.cmd}</Cmd></td>
              <td className="py-2 text-slate-600">{r.desc}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export default function Manual() {
  const [state, setState] = useState<Record<string, boolean>>(() => {
    try { return JSON.parse(localStorage.getItem(LS_KEY) ?? '{}'); } catch { return {}; }
  });
  useEffect(() => {
    try { localStorage.setItem(LS_KEY, JSON.stringify(state)); } catch { /* noop */ }
  }, [state]);

  const done = useMemo(() => ALL_IDS.filter((id) => state[id]).length, [state]);
  const toggle = (id: string) => setState((s) => ({ ...s, [id]: !s[id] }));
  const secProgress = (sec: SectionDef) => {
    const ids = sec.cards.flatMap((c) => (c.checks ?? []).map((k) => k.id));
    if (!ids.length) return null;
    return `${ids.filter((id) => state[id]).length}/${ids.length}`;
  };

  return (
    <div className="space-y-8 max-w-3xl">
      <div>
        <p className="text-[11px] uppercase tracking-widest text-emerald-700 font-bold">Manual de usuario</p>
        <h1 className="text-xl md:text-2xl font-bold mt-1">Todo lo que tiene el sistema y cómo dejarlo configurado</h1>
        <p className="text-sm text-slate-500 mt-1">
          Cada sección lista qué hace cada pantalla y una checklist de configuración. Los tildes se
          guardan en este navegador — usalo como inventario de qué te falta prender.
        </p>
        <div className="card p-4 mt-4">
          <div className="flex items-baseline justify-between">
            <span className="font-semibold text-sm">Configuración general</span>
            <span className="font-mono text-xs text-slate-500 tabular-nums">{done}/{ALL_IDS.length} ítems tildados</span>
          </div>
          <div className="h-2 rounded-full bg-slate-200 mt-2 overflow-hidden">
            <div className="h-full bg-emerald-500 rounded-full transition-all" style={{ width: `${ALL_IDS.length ? (100 * done) / ALL_IDS.length : 0}%` }} />
          </div>
        </div>
        {/* índice */}
        <div className="flex flex-wrap gap-1.5 mt-3">
          {[...SECTIONS.map((s) => ({ id: s.id, t: s.title.split(':')[0], p: secProgress(s) })),
            { id: 'whatsapp', t: 'Comandos por WhatsApp', p: null },
            { id: 'apagado', t: 'Apagado / sin usar', p: null }].map((s) => (
            <a key={s.id} href={`#${s.id}`}
               className="text-xs border border-slate-200 rounded-full px-2.5 py-1 text-slate-600 hover:bg-white hover:text-slate-900">
              {s.t}{s.p ? <span className="ml-1 font-mono text-emerald-700">{s.p}</span> : null}
            </a>
          ))}
        </div>
      </div>

      {/* mapa en un minuto */}
      <div className="card p-4">
        <p className="text-[11px] uppercase tracking-widest text-slate-400 font-bold">Mapa en un minuto</p>
        <div className="flex flex-wrap items-center gap-1.5 mt-2 text-[13px] font-medium">
          {['1 · Captación', '2 · Asignación por zona', '3 · Cadencia humanizada', '4 · Conversación (bot IA)', '5 · Alta / demo', '6 · App provisionada'].map((s, i) => (
            <span key={s} className="flex items-center gap-1.5">
              {i > 0 && <span className="text-slate-400">→</span>}
              <span className="bg-slate-100 border border-slate-200 rounded-lg px-2.5 py-1">{s}</span>
            </span>
          ))}
        </div>
        <p className="text-sm text-slate-500 mt-3">
          Los leads entran por scraping (Places/Apify), anuncios (Meta forms y click-to-WhatsApp), importación
          manual o push de las apps. Se reparten a vendedores según <b>whitelist de producto + zonas + WhatsApp
          conectado</b>. La cadencia del producto les escribe con ritmo humano; cuando responden, el bot IA
          contesta, califica y — si el onboarding está prendido — crea la cuenta solo. Vos podés tomar cualquier
          conversación a mano cuando quieras.
        </p>
        <p className="text-sm mt-2 border-l-2 border-blue-400 bg-blue-50 rounded-r-lg px-3 py-2">
          Regla de oro para debuggear "no está saliendo nada": mirá el <b>Centro de control</b> en <Cmd>/admin</Cmd> —
          el badge "No está vendiendo" te dice exactamente qué falta.
        </p>
      </div>

      {SECTIONS.map((sec) => (
        <section key={sec.id} id={sec.id} className="scroll-mt-4 space-y-3">
          <div>
            <p className="text-[11px] uppercase tracking-widest text-slate-400 font-bold">{sec.eyebrow}</p>
            <h2 className="text-lg font-bold">{sec.title}</h2>
            {sec.intro && <p className="text-sm text-slate-500 mt-0.5">{sec.intro}</p>}
          </div>
          {sec.cards.map((c) => (
            <div key={c.title} className="card p-4">
              <h3 className="font-semibold text-[15px] flex items-center gap-2 flex-wrap">
                {c.title}
                {c.routes?.map((r) => (
                  <span key={r} className="font-mono text-[11px] bg-emerald-50 text-emerald-700 rounded px-1.5 py-0.5 font-semibold">{r}</span>
                ))}
                {c.admin && <span className="text-[10px] uppercase tracking-wide font-bold bg-blue-50 text-blue-600 rounded px-1.5 py-0.5">admin</span>}
              </h3>
              {c.desc && <p className="text-sm text-slate-500 mt-1.5">{c.desc}</p>}
              {c.actions && (
                <details className="mt-2 border-t border-dashed border-slate-200 pt-2">
                  <summary className="text-[13px] font-semibold text-slate-500 cursor-pointer select-none">Qué tiene / acciones</summary>
                  <ul className="list-disc pl-5 mt-2 space-y-1 text-sm text-slate-600">
                    {c.actions.map((a, i) => <li key={i}>{a}</li>)}
                  </ul>
                </details>
              )}
              {c.checks && c.checks.length > 0 && (
                <div className="mt-3">
                  <p className="text-[11px] uppercase tracking-widest text-emerald-700 font-bold mb-1">Configuración</p>
                  <div className="text-sm">
                    {c.checks.map((k) => (
                      <CheckRow key={k.id} item={k} checked={!!state[k.id]} onToggle={() => toggle(k.id)} />
                    ))}
                  </div>
                </div>
              )}
              {c.note && <p className="text-[13px] mt-3 border-l-2 border-amber-400 bg-amber-50 rounded-r-lg px-3 py-2">{c.note}</p>}
              {c.tip && <p className="text-[13px] mt-3 border-l-2 border-blue-400 bg-blue-50 rounded-r-lg px-3 py-2">{c.tip}</p>}
            </div>
          ))}
        </section>
      ))}

      {/* comandos whatsapp */}
      <section id="whatsapp" className="scroll-mt-4 space-y-3">
        <div>
          <p className="text-[11px] uppercase tracking-widest text-slate-400 font-bold">Sin abrir el dashboard</p>
          <h2 className="text-lg font-bold">Comandos por WhatsApp</h2>
          <p className="text-sm text-slate-500 mt-0.5">
            Dos contextos: <b>chats de leads</b> (takeover del bot) y <b>tu número maestro</b> (ideas/PDF/transcripción).
          </p>
        </div>
        <div className="card p-4">
          <h3 className="font-semibold text-[15px]">Takeover del bot — en el chat de un lead</h3>
          <CmdTable rows={WA_TAKEOVER} />
          <p className="text-[13px] mt-3 border-l-2 border-amber-400 bg-amber-50 rounded-r-lg px-3 py-2">
            Los <b>mensajes de sistema de las apps</b> (OTP "tu código para entrar…", reportes "📊 SESIÓN…") NO
            pausan el bot — se registran y listo. Los ecos de lo que manda el propio bot tampoco.
          </p>
        </div>
        <div className="card p-4">
          <h3 className="font-semibold text-[15px]">Intake maestro — desde tu número, a la línea configurada</h3>
          <p className="text-sm text-slate-500 mt-1">Config en /inspiracion ("Entrada por WhatsApp") y /transcripcion. El bot responde siempre con prefijo 💡. Sesiones de 30 min.</p>
          <CmdTable rows={WA_MASTER} />
        </div>
        <div className="card p-4">
          <h3 className="font-semibold text-[15px]">Lo que hace solo (lado del lead)</h3>
          <ul className="list-disc pl-5 mt-2 space-y-1 text-sm text-slate-600">
            <li><b>Lead de anuncio</b>: número desconocido que escribe "activar/quiero/me interesa… [App]" → crea el lead WhatsAppAd y arranca el onboarding.</li>
            <li><b>Notas de voz de leads</b>: se transcriben solas (Groq/Whisper, 3 intentos) y el bot responde al texto.</li>
            <li><b>Ráfagas</b>: el bot espera ~12 s de silencio antes de contestar (no responde a mitad de una ráfaga).</li>
            <li><b>Post-alta</b>: 72 h de gracia atendiendo al lead recién provisionado ("pasame el link de nuevo") aunque ya esté Cerrado.</li>
          </ul>
        </div>
      </section>

      {/* apagado / sin usar */}
      <section id="apagado" className="scroll-mt-4 space-y-3">
        <div>
          <p className="text-[11px] uppercase tracking-widest text-slate-400 font-bold">Inventario honesto</p>
          <h2 className="text-lg font-bold">Apagado o sin usar (al 7 jul 2026)</h2>
          <p className="text-sm text-slate-500 mt-0.5">
            Features construidas que hoy no están rindiendo. Verificá el estado real en el Centro de control — esto es la última foto.
          </p>
        </div>
        <div className="card p-4 overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-[10px] uppercase tracking-wider text-slate-400 border-b">
                <th className="py-1.5 pr-3">Feature</th>
                <th className="py-1.5 pr-3">Estado</th>
                <th className="py-1.5">Qué falta para usarla</th>
              </tr>
            </thead>
            <tbody>
              {OFF_TABLE.map((r, i) => (
                <tr key={i} className="border-b border-slate-100 align-top">
                  <td className="py-2 pr-3">{r.f}</td>
                  <td className="py-2 pr-3">
                    <span className={`text-[11px] font-bold rounded-full px-2 py-0.5 whitespace-nowrap ${r.on ? 'bg-emerald-50 text-emerald-700' : 'bg-amber-50 text-amber-700'}`}>
                      {r.estado}
                    </span>
                  </td>
                  <td className="py-2 text-slate-600">{r.falta}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <p className="text-xs text-slate-400 border-t pt-3">
        Manual generado del código real (31 pantallas + webhook + flags). Los tildes se guardan solo en este navegador.
      </p>
    </div>
  );
}
