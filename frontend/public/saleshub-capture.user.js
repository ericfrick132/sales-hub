// ==UserScript==
// @name         SalesHub Maps Capture
// @namespace    saleshub
// @version      0.4.0
// @description  Captura negocios desde Google Maps y los manda a SalesHub como leads.
// @match        https://www.google.com/maps/*
// @match        https://maps.google.com/*
// @updateURL    https://sales.efcloud.tech/saleshub-capture.user.js
// @downloadURL  https://sales.efcloud.tech/saleshub-capture.user.js
// @grant        GM_setValue
// @grant        GM_getValue
// @grant        GM_xmlhttpRequest
// @connect      *
// @run-at       document-idle
// ==/UserScript==

/* eslint-disable */
(function () {
  'use strict';

  // ============================================================
  // Config + storage
  // ============================================================

  const DEFAULT_API = 'https://api.sales.efcloud.tech';

  // Migración silenciosa: el dominio del frontend (sales.efcloud.tech) es GitHub Pages
  // estático y devuelve 405 a cualquier POST. Forzamos al dominio del backend.
  const storedApi = (GM_getValue('saleshub.api', '') || '').replace(/\/$/, '');
  if (!storedApi || storedApi === 'https://sales.efcloud.tech' || storedApi === 'http://sales.efcloud.tech') {
    GM_setValue('saleshub.api', DEFAULT_API);
  }

  const cfg = {
    api: GM_getValue('saleshub.api', DEFAULT_API),
    token: GM_getValue('saleshub.token', ''),
    user: GM_getValue('saleshub.user', null),
    productKey: GM_getValue('saleshub.productKey', ''),
    localityGid2: GM_getValue('saleshub.localityGid2', ''),
    category: GM_getValue('saleshub.category', '')
  };

  // saleshub:productKey=...|gid2=...|cat=... en el hash autocompleta el contexto.
  const hash = decodeURIComponent(location.hash || '');
  const tag = hash.match(/saleshub:(.*)$/)?.[1];
  if (tag) {
    for (const part of tag.split('|')) {
      const [k, v] = part.split('=');
      if (k === 'productKey' && v) cfg.productKey = v;
      if (k === 'gid2' && v) cfg.localityGid2 = v;
      if (k === 'cat' && v) cfg.category = v;
    }
  }

  let buffer = GM_getValue('saleshub.buffer', []);
  let products = GM_getValue('saleshub.products', []);
  let lastResult = GM_getValue('saleshub.lastResult', null);
  let collapsed = GM_getValue('saleshub.collapsed', false);
  let preview = false;
  let autoRunning = false;
  let autoCancel = false;
  let autoStatus = '';

  function persistBuffer() { GM_setValue('saleshub.buffer', buffer); }

  // ============================================================
  // API
  // ============================================================

  function apiCall(method, path, body) {
    return new Promise((resolve, reject) => {
      GM_xmlhttpRequest({
        method,
        url: `${cfg.api}${path}`,
        headers: {
          'Content-Type': 'application/json',
          ...(cfg.token ? { Authorization: `Bearer ${cfg.token}` } : {})
        },
        data: body ? JSON.stringify(body) : null,
        onload: (res) => {
          let data = null;
          try { data = JSON.parse(res.responseText); } catch {}
          if (res.status >= 200 && res.status < 300) return resolve(data);
          // Mensajes accionables por status.
          let msg = data?.error || `HTTP ${res.status}`;
          if (res.status === 401 || res.status === 403) {
            msg = 'Sesión expirada — iniciá sesión otra vez';
          } else if (res.status === 404 || res.status === 405) {
            msg = `URL del backend mal (${cfg.api}). Click "Config" y corregí.`;
          } else if (res.status >= 500) {
            msg = `Backend caído (${res.status}). Avisá al admin.`;
          }
          reject({ status: res.status, message: msg, data });
        },
        onerror: () => reject({ status: 0, message: 'Sin conexión al backend' }),
        ontimeout: () => reject({ status: 0, message: 'Timeout — el backend no responde' })
      });
    });
  }

  // ============================================================
  // Extractores DOM de Google Maps
  // ============================================================

  function readDetailPanel() {
    const main = document.querySelector('div[role="main"][aria-label]:not([aria-label="Mapa"]):not([aria-label*="Map"])')
              || document.querySelector('[role="main"]:not([aria-label="Mapa"])');
    if (!main) return null;
    const name = main.getAttribute('aria-label') || main.querySelector('h1')?.innerText?.trim();
    if (!name) return null;

    const phone = labelOf(main, /^Teléfono:\s*(.+)$/, /^Phone:\s*(.+)$/);
    const address = labelOf(main, /^Dirección:\s*(.+)$/, /^Address:\s*(.+)$/);
    const website = main.querySelector('a[data-item-id="authority"]')?.href
                 || main.querySelector('a[aria-label^="Sitio web"]')?.href
                 || null;

    const ratingNode = main.querySelector('[aria-label*="estrellas"], [aria-label*="stars"]');
    let rating = null, reviews = null;
    if (ratingNode) {
      const m = (ratingNode.getAttribute('aria-label') || '').match(/(\d+[\.,]?\d*)/);
      if (m) rating = parseFloat(m[1].replace(',', '.'));
      const reviewsNode = ratingNode.parentElement?.querySelector('[aria-label*="reseñas"], [aria-label*="reviews"]')
                       || main.querySelector('[aria-label*="reseñas"], [aria-label*="reviews"]');
      const rm = (reviewsNode?.getAttribute?.('aria-label') || reviewsNode?.innerText || '').match(/(\d[\d\.,]*)/);
      if (rm) reviews = parseInt(rm[1].replace(/[\.,]/g, ''), 10) || null;
    }

    const type = main.querySelector('button[jsaction*="category"]')?.innerText?.trim() || null;
    const latlng = location.href.match(/@(-?\d+\.\d+),(-?\d+\.\d+)/);
    return {
      name, phone, address, website, rating, totalReviews: reviews, type,
      businessStatus: null,
      latitude: latlng ? parseFloat(latlng[1]) : null,
      longitude: latlng ? parseFloat(latlng[2]) : null
    };
  }

  function labelOf(root, ...regexes) {
    const els = root.querySelectorAll('[aria-label]');
    for (const el of els) {
      const lbl = el.getAttribute('aria-label') || '';
      for (const rx of regexes) {
        const m = lbl.match(rx);
        if (m) return m[1].trim();
      }
    }
    return null;
  }

  // Devuelve el contenedor scrolleable de los resultados.
  function getFeedEl() {
    return document.querySelector('div[role="feed"]');
  }

  function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

  // Auto-scrollea el feed hasta que Google deja de cargar más items.
  // Detecta el final de dos formas: (a) el contador de items se estabiliza
  // por N iteraciones, o (b) Maps muestra "Has llegado al final de la lista".
  async function autoScrollFeed(onProgress) {
    const feed = getFeedEl();
    if (!feed) return { ok: false, reason: 'no-feed' };

    let stable = 0;
    let lastCount = -1;
    const STABLE_THRESHOLD = 4;     // 4 ciclos sin items nuevos = fin
    const STEP_MS = 900;            // espera entre scrolls
    const MAX_ITERATIONS = 80;      // tope de seguridad ~72s

    for (let i = 0; i < MAX_ITERATIONS; i++) {
      if (autoCancel) return { ok: false, reason: 'canceled', count: lastCount };

      // Sentinel "Has llegado al final" / "Estás llegando al final".
      const finalText = feed.innerText || '';
      const reachedEnd = /llegado al final|reached the end|fin de la lista|end of the list/i.test(finalText);

      const count = feed.querySelectorAll('a[href*="/maps/place/"]').length;
      onProgress?.(count, reachedEnd);

      if (count === lastCount) {
        stable++;
      } else {
        stable = 0;
        lastCount = count;
      }
      if (reachedEnd && stable >= 1) break;
      if (stable >= STABLE_THRESHOLD) break;

      // Scroll al fondo (varía levemente para evitar detección como bot).
      feed.scrollTop = feed.scrollHeight;
      await sleep(STEP_MS);
    }
    return { ok: true, count: lastCount };
  }

  function readFeedList() {
    // Descendant selector — Google a veces anida las cards más profundo.
    const links = document.querySelectorAll('div[role="feed"] a[href*="/maps/place/"]');
    const out = [];
    const seenHref = new Set();
    links.forEach(a => {
      const href = a.getAttribute('href');
      if (href && seenHref.has(href)) return;
      if (href) seenHref.add(href);

      const card = a.closest('div[role="article"]') || a.parentElement?.parentElement || a.parentElement;
      const name = a.getAttribute('aria-label') || card?.querySelector('div[role="heading"]')?.innerText?.trim();
      if (!name) return;
      const text = (card?.innerText || '').replace(/\s+/g, ' ').trim();
      const phoneM = text.match(/(\+?\d[\d\s\-]{7,}\d)/);
      const phone = phoneM ? phoneM[1].trim() : null;
      const ratingM = text.match(/(\d+[\.,]\d+)\s*\((\d[\d\.,]*)\)/);
      const rating = ratingM ? parseFloat(ratingM[1].replace(',', '.')) : null;
      const reviews = ratingM ? parseInt(ratingM[2].replace(/[\.,]/g, ''), 10) : null;
      let type = null, address = null;
      const dotParts = text.split(' · ');
      if (dotParts.length >= 2) { type = dotParts[0]?.trim() || null; address = dotParts[dotParts.length - 1]?.trim() || null; }
      out.push({
        name, phone, address, website: null, rating, totalReviews: reviews,
        type, businessStatus: null, latitude: null, longitude: null
      });
    });
    return out;
  }

  function addToBuffer(item) {
    const key = `${(item.name || '').toLowerCase().trim()}|${(item.phone || '').replace(/\D/g, '')}`;
    if (buffer.some(b => `${(b.name || '').toLowerCase().trim()}|${(b.phone || '').replace(/\D/g, '')}` === key)) return false;
    buffer.push(item);
    persistBuffer();
    return true;
  }

  function getCurrentQuery() {
    const m = location.pathname.match(/\/maps\/search\/([^/]+)/);
    if (m) return decodeURIComponent(m[1]).replace(/\+/g, ' ');
    return '(captura ad-hoc)';
  }

  // ============================================================
  // UI
  // ============================================================

  const styles = `
    #sh-panel { position:fixed; bottom:16px; right:16px; z-index:99999;
      background:#fff; border:1px solid #cbd5e1; border-radius:10px;
      box-shadow:0 8px 24px rgba(15,23,42,.18);
      font:12px -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;
      color:#0f172a; width:320px; max-height:calc(100vh - 80px); overflow:auto; }
    #sh-panel.collapsed { width:auto; padding:0; }
    #sh-panel * { box-sizing:border-box; }
    #sh-panel header { display:flex; align-items:center; gap:8px;
      padding:10px 12px; border-bottom:1px solid #e2e8f0; }
    #sh-panel header .title { font-weight:600; font-size:13px; flex:1; }
    #sh-panel header button { background:none; border:0; cursor:pointer; color:#64748b; font-size:16px; line-height:1; padding:2px 6px; }
    #sh-panel header button:hover { color:#0f172a; }
    #sh-panel .body { padding:10px 12px; display:flex; flex-direction:column; gap:10px; }
    #sh-panel .row { display:flex; align-items:center; gap:6px; flex-wrap:wrap; }
    #sh-panel .status-ok { color:#15803d; font-weight:500; }
    #sh-panel .status-bad { color:#b91c1c; font-weight:500; }
    #sh-panel select, #sh-panel input { font:inherit; padding:5px 7px; border:1px solid #cbd5e1; border-radius:5px; background:#fff; flex:1; min-width:0; }
    #sh-panel button.btn { font:inherit; padding:7px 10px; border-radius:6px; border:1px solid #cbd5e1; background:#fff; cursor:pointer; color:#0f172a; }
    #sh-panel button.btn:hover { background:#f1f5f9; }
    #sh-panel button.btn:disabled { opacity:.5; cursor:not-allowed; }
    #sh-panel button.primary { background:#2563eb; border-color:#2563eb; color:#fff; font-weight:500; }
    #sh-panel button.primary:hover:not(:disabled) { background:#1d4ed8; }
    #sh-panel button.success { background:#16a34a; border-color:#16a34a; color:#fff; font-weight:500; }
    #sh-panel button.success:hover:not(:disabled) { background:#15803d; }
    #sh-panel button.ghost { background:transparent; border-color:transparent; color:#64748b; padding:4px 6px; font-size:11px; }
    #sh-panel button.ghost:hover { color:#0f172a; background:#f1f5f9; }
    #sh-panel .pill { display:inline-flex; align-items:center; gap:4px; background:#f1f5f9; border-radius:999px; padding:2px 8px; font-size:11px; }
    #sh-panel .buffer-count { background:#1e293b; color:#fff; border-radius:999px; padding:2px 8px; font-weight:600; font-size:11px; }
    #sh-panel .buffer-count.pulse { animation:sh-pulse .6s ease; }
    @keyframes sh-pulse { 0%{transform:scale(1)} 50%{transform:scale(1.25); background:#16a34a} 100%{transform:scale(1)} }
    @keyframes sh-blink { 0%,100%{opacity:1} 50%{opacity:.3} }
    #sh-panel ul.preview { list-style:none; margin:0; padding:0; max-height:220px; overflow-y:auto; border:1px solid #e2e8f0; border-radius:6px; }
    #sh-panel ul.preview li { padding:6px 8px; border-bottom:1px solid #f1f5f9; display:grid; grid-template-columns:1fr auto; gap:4px; font-size:11px; }
    #sh-panel ul.preview li:last-child { border-bottom:0; }
    #sh-panel ul.preview .name { font-weight:600; color:#0f172a; word-break:break-word; }
    #sh-panel ul.preview .meta { color:#64748b; font-size:10px; word-break:break-word; }
    #sh-panel ul.preview .rm { background:none; border:0; color:#94a3b8; cursor:pointer; font-size:14px; padding:0 4px; }
    #sh-panel ul.preview .rm:hover { color:#dc2626; }
    #sh-panel .alert { padding:8px 10px; border-radius:6px; font-size:11px; }
    #sh-panel .alert.warn { background:#fef3c7; color:#92400e; border:1px solid #fde68a; }
    #sh-panel .alert.ok { background:#dcfce7; color:#166534; border:1px solid #bbf7d0; }
    #sh-panel .alert.err { background:#fee2e2; color:#991b1b; border:1px solid #fecaca; }
    #sh-panel .mini { padding:8px 12px; cursor:pointer; display:flex; align-items:center; gap:8px; user-select:none; }
    #sh-panel .mini b { font-size:13px; }
    #sh-toast { position:fixed; bottom:96px; right:16px; z-index:100000; max-width:300px;
      background:#0f172a; color:#fff; padding:10px 14px; border-radius:8px; font:12px sans-serif; box-shadow:0 4px 12px rgba(0,0,0,.2); }
    #sh-toast.err { background:#991b1b; }
    #sh-toast.ok { background:#166534; }
  `;
  const styleEl = document.createElement('style');
  styleEl.textContent = styles;
  document.head.appendChild(styleEl);

  const panel = document.createElement('div');
  panel.id = 'sh-panel';
  document.body.appendChild(panel);

  function toast(msg, kind = '') {
    const old = document.getElementById('sh-toast');
    if (old) old.remove();
    const t = document.createElement('div');
    t.id = 'sh-toast';
    if (kind) t.className = kind;
    t.textContent = msg;
    document.body.appendChild(t);
    setTimeout(() => t.remove(), 3000);
  }

  function pulseBuffer() {
    const b = panel.querySelector('.buffer-count');
    if (!b) return;
    b.classList.remove('pulse');
    void b.offsetWidth;
    b.classList.add('pulse');
  }

  function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
  }

  function render() {
    if (collapsed) {
      panel.classList.add('collapsed');
      panel.innerHTML = `<div class="mini" id="sh-expand">📋 <b>SH</b> <span class="buffer-count">${buffer.length}</span></div>`;
      panel.querySelector('#sh-expand').onclick = () => { collapsed = false; GM_setValue('saleshub.collapsed', false); render(); };
      return;
    }
    panel.classList.remove('collapsed');

    const loggedIn = !!cfg.token && !!cfg.user;
    const ready = loggedIn && !!cfg.productKey;
    const productOptions = (products || []).map(p =>
      `<option value="${escapeHtml(p.productKey)}" ${p.productKey === cfg.productKey ? 'selected' : ''}>${escapeHtml(p.displayName)}</option>`
    ).join('');

    panel.innerHTML = `
      <header>
        <div class="title">SalesHub · captura</div>
        <button id="sh-min" title="Minimizar">_</button>
      </header>
      <div class="body">
        ${loggedIn
          ? `<div class="row">
               <span class="status-ok">✓ ${escapeHtml(cfg.user.displayName)}</span>
               <button class="ghost" id="sh-logout" style="margin-left:auto;">cerrar sesión</button>
             </div>`
          : `<div class="alert warn">⚠ No iniciaste sesión todavía. Sin esto no podés subir nada.</div>
             <button class="btn primary" id="sh-login">Iniciar sesión</button>`}

        ${loggedIn ? `
          <div class="row">
            <label style="color:#64748b;">Producto:</label>
            ${products.length === 0
              ? `<span style="color:#b91c1c;">no se cargaron productos</span> <button class="ghost" id="sh-reload-products">reintentar</button>`
              : `<select id="sh-product">${productOptions}</select>`}
          </div>
          ${getCurrentQuery() !== '(captura ad-hoc)'
            ? `<div class="row"><span class="pill">🔍 ${escapeHtml(getCurrentQuery())}</span></div>`
            : ''}
        ` : ''}

        <div class="row">
          <span>Buffer</span>
          <span class="buffer-count">${buffer.length}</span>
          ${buffer.length > 0 ? `<button class="ghost" id="sh-toggle-preview" style="margin-left:auto;">${preview ? '▴ ocultar' : '▾ ver'}</button>` : ''}
        </div>

        ${preview && buffer.length > 0 ? `
          <ul class="preview">
            ${buffer.map((b, i) => `
              <li>
                <div>
                  <div class="name">${escapeHtml(b.name)}</div>
                  <div class="meta">
                    ${b.phone ? `📞 ${escapeHtml(b.phone)}` : '<i style="color:#cbd5e1;">sin teléfono</i>'}
                    ${b.rating ? ` · ⭐ ${b.rating}${b.totalReviews ? ` (${b.totalReviews})` : ''}` : ''}
                  </div>
                  ${b.address ? `<div class="meta">${escapeHtml(b.address)}</div>` : ''}
                </div>
                <button class="rm" data-i="${i}" title="Quitar">×</button>
              </li>
            `).join('')}
          </ul>
        ` : ''}

        ${autoRunning ? `
          <div class="alert ok" style="display:flex; align-items:center; gap:8px;">
            <span style="display:inline-block; width:10px; height:10px; border-radius:999px; background:#16a34a; animation:sh-blink 1s infinite;"></span>
            <span style="flex:1;">${escapeHtml(autoStatus || 'Cargando…')}</span>
            <button class="ghost" id="sh-cancel-auto" style="color:#991b1b;">cancelar</button>
          </div>
        ` : `
          <div class="row">
            <button class="btn primary" id="sh-auto" style="flex:1;">🔁 Auto-capturar todo</button>
          </div>
          <div class="row">
            <button class="btn" id="sh-add-list" style="flex:1;">📋 Solo lo visible</button>
            <button class="btn" id="sh-add-detail">📍 Este lugar</button>
          </div>
        `}

        <div class="row">
          <button class="btn success" id="sh-upload" style="flex:1;" ${buffer.length === 0 || !ready ? 'disabled' : ''}>
            ⬆ Subir ${buffer.length} ${buffer.length === 1 ? 'lead' : 'leads'}
          </button>
          <button class="btn" id="sh-clear" ${buffer.length === 0 ? 'disabled' : ''}>Vaciar</button>
        </div>

        ${!ready && loggedIn ? `<div class="alert warn">Elegí un producto antes de subir.</div>` : ''}

        ${lastResult ? `
          <div class="alert ok">
            ✓ Última subida: <b>${lastResult.created ?? 0}</b> nuevos · ${lastResult.duplicates ?? 0} dup · ${lastResult.skipped ?? 0} skip
          </div>
        ` : ''}

        <div class="row">
          <button class="ghost" id="sh-cfg" style="margin-left:auto;">⚙ Config avanzada</button>
        </div>
      </div>
    `;

    bind();
  }

  function bind() {
    const $ = (sel) => panel.querySelector(sel);

    $('#sh-min').onclick = () => { collapsed = true; GM_setValue('saleshub.collapsed', true); render(); };

    if ($('#sh-login')) $('#sh-login').onclick = login;
    if ($('#sh-logout')) $('#sh-logout').onclick = logout;
    if ($('#sh-reload-products')) $('#sh-reload-products').onclick = loadProducts;

    if ($('#sh-product')) $('#sh-product').onchange = (e) => {
      cfg.productKey = e.target.value;
      GM_setValue('saleshub.productKey', cfg.productKey);
      render();
    };

    if ($('#sh-toggle-preview')) $('#sh-toggle-preview').onclick = () => { preview = !preview; render(); };

    if ($('#sh-auto')) $('#sh-auto').onclick = autoCapture;
    if ($('#sh-cancel-auto')) $('#sh-cancel-auto').onclick = () => { autoCancel = true; };

    if ($('#sh-add-list')) $('#sh-add-list').onclick = () => {
      const list = readFeedList();
      if (list.length === 0) {
        return toast('No detecté la lista lateral. Asegurate de estar en una búsqueda con resultados visibles.', 'err');
      }
      let added = 0;
      list.forEach(it => { if (addToBuffer(it)) added++; });
      toast(added > 0 ? `+ ${added} agregados (${list.length - added} ya estaban)` : `Ya tenías los ${list.length}`, added > 0 ? 'ok' : '');
      render();
      pulseBuffer();
    };

    if ($('#sh-add-detail')) $('#sh-add-detail').onclick = () => {
      const it = readDetailPanel();
      if (!it) return toast('No detecté un panel de detalle. Click un negocio en la lista primero.', 'err');
      const ok = addToBuffer(it);
      toast(ok ? `+ ${it.name}` : `Ya estaba: ${it.name}`, ok ? 'ok' : '');
      render();
      if (ok) pulseBuffer();
    };

    if ($('#sh-upload')) $('#sh-upload').onclick = upload;

    if ($('#sh-clear')) $('#sh-clear').onclick = () => {
      if (!confirm(`¿Tirar los ${buffer.length} items del buffer? (No se sube nada.)`)) return;
      buffer = []; persistBuffer(); render(); toast('Buffer vacío');
    };

    panel.querySelectorAll('.rm[data-i]').forEach(b => {
      b.onclick = () => {
        const i = parseInt(b.getAttribute('data-i'), 10);
        const removed = buffer[i];
        buffer.splice(i, 1);
        persistBuffer();
        render();
        toast(`Quitado: ${removed?.name ?? ''}`);
      };
    });

    if ($('#sh-cfg')) $('#sh-cfg').onclick = openAdvancedConfig;
  }

  // ============================================================
  // Acciones
  // ============================================================

  async function login() {
    const email = prompt('Email de tu cuenta SalesHub:', cfg.user?.email ?? '');
    if (!email) return;
    const password = prompt('Contraseña:');
    if (!password) return;
    try {
      const data = await apiCall('POST', '/api/auth/login', { email: email.trim(), password });
      cfg.token = data.accessToken;
      cfg.user = { sellerId: data.sellerId, displayName: data.displayName, email: data.email };
      GM_setValue('saleshub.token', cfg.token);
      GM_setValue('saleshub.user', cfg.user);
      toast(`Hola ${data.displayName}`, 'ok');
      await loadProducts();
      render();
    } catch (err) {
      toast(`Login falló: ${err.message}`, 'err');
    }
  }

  function logout() {
    if (!confirm('¿Cerrar sesión? El buffer no se borra.')) return;
    cfg.token = '';
    cfg.user = null;
    GM_setValue('saleshub.token', '');
    GM_setValue('saleshub.user', null);
    render();
    toast('Sesión cerrada');
  }

  async function loadProducts() {
    if (!cfg.token) return;
    try {
      const data = await apiCall('GET', '/api/products', null);
      products = (data || []).filter(p => p.active);
      GM_setValue('saleshub.products', products);
      if (!cfg.productKey && products.length > 0) {
        cfg.productKey = products[0].productKey;
        GM_setValue('saleshub.productKey', cfg.productKey);
      }
      render();
    } catch (err) {
      toast(`No pude cargar productos: ${err.message}`, 'err');
    }
  }

  async function upload() {
    if (buffer.length === 0) return;
    if (!cfg.token || !cfg.productKey) return toast('Falta sesión o producto', 'err');
    const btn = panel.querySelector('#sh-upload');
    if (btn) { btn.disabled = true; btn.textContent = 'Subiendo…'; }
    try {
      const data = await apiCall('POST', '/api/search-jobs', {
        productKey: cfg.productKey,
        localityGid2: cfg.localityGid2 || null,
        category: cfg.category || null,
        query: getCurrentQuery(),
        items: buffer
      });
      lastResult = {
        created: data?.leadsCreated ?? 0,
        duplicates: data?.duplicates ?? 0,
        skipped: data?.skipped ?? 0,
        when: Date.now()
      };
      GM_setValue('saleshub.lastResult', lastResult);
      toast(`✓ ${lastResult.created} nuevos · ${lastResult.duplicates} dup · ${lastResult.skipped} skip`, 'ok');
      buffer = []; persistBuffer();
      render();
    } catch (err) {
      render();
      toast(`Error subiendo: ${err.message}`, 'err');
    }
  }

  async function autoCapture() {
    const feed = getFeedEl();
    if (!feed) return toast('No detecté la lista. Hacé una búsqueda en Maps primero (la columna "Resultados").', 'err');

    autoCancel = false;
    autoRunning = true;
    autoStatus = 'Iniciando scroll…';
    render();

    const result = await autoScrollFeed((count, reachedEnd) => {
      autoStatus = reachedEnd
        ? `Cargando últimos… (${count} encontrados)`
        : `Scrolleando… ${count} resultados cargados`;
      // Re-render solo el alert para no perder el foco ni reconstruir todo.
      const alertEl = panel.querySelector('.alert.ok span:nth-child(2)');
      if (alertEl) alertEl.textContent = autoStatus;
    });

    if (!result.ok && result.reason === 'no-feed') {
      autoRunning = false; render();
      return toast('Perdí el listado mientras corría. Probá de nuevo.', 'err');
    }
    if (!result.ok && result.reason === 'canceled') {
      autoRunning = false; render();
      return toast(`Cancelado a ${result.count ?? 0} items.`);
    }

    // Volver al tope para que el siguiente scan no se confunda y para UX.
    feed.scrollTop = 0;
    await sleep(150);

    const all = readFeedList();
    let added = 0;
    all.forEach(it => { if (addToBuffer(it)) added++; });

    autoRunning = false;
    autoStatus = '';
    render();
    pulseBuffer();
    toast(
      added > 0
        ? `✓ ${added} agregados (${all.length - added} ya estaban, total listado: ${all.length})`
        : `Ya tenías los ${all.length} del listado`,
      added > 0 ? 'ok' : ''
    );
  }

  function openAdvancedConfig() {
    const api = prompt('URL del backend (avanzado, dejalo así si no sabés):', cfg.api);
    if (api == null) return;
    cfg.api = api.replace(/\/$/, '');
    GM_setValue('saleshub.api', cfg.api);
    const gid2 = prompt('Locality GID2 (opcional, para asignar el job a una localidad):', cfg.localityGid2);
    if (gid2 != null) { cfg.localityGid2 = gid2; GM_setValue('saleshub.localityGid2', gid2); }
    const cat = prompt('Categoría override (opcional):', cfg.category);
    if (cat != null) { cfg.category = cat; GM_setValue('saleshub.category', cat); }
    toast('Config guardada');
    render();
  }

  // ============================================================
  // Boot
  // ============================================================

  if (cfg.token) loadProducts();
  render();

  // Sync entre tabs.
  setInterval(() => {
    const fresh = GM_getValue('saleshub.buffer', []);
    if (fresh.length !== buffer.length) { buffer = fresh; render(); }
  }, 2000);
})();
