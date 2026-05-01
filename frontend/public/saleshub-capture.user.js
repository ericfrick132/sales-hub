// ==UserScript==
// @name         SalesHub Maps Capture
// @namespace    saleshub
// @version      0.2.0
// @description  Captura negocios desde Google Maps (logueado) y los manda a SalesHub como leads.
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

  // Default a backend (api.sales.efcloud.tech). Antes apuntaba al dominio del frontend
  // (sales.efcloud.tech) que es GitHub Pages estático y devuelve 405 a los POST.
  const DEFAULT_API = 'https://api.sales.efcloud.tech';

  // Migración silenciosa: si el storage tiene el dominio viejo (frontend), lo reescribimos al nuevo (backend).
  const storedApi = (GM_getValue('saleshub.api', '') || '').replace(/\/$/, '');
  if (!storedApi || storedApi === 'https://sales.efcloud.tech' || storedApi === 'http://sales.efcloud.tech') {
    GM_setValue('saleshub.api', DEFAULT_API);
  }

  // -------- Config (editable desde el panel) ----------------------------
  const cfg = {
    api: GM_getValue('saleshub.api', DEFAULT_API),
    token: GM_getValue('saleshub.token', ''),
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

  // -------- Buffer en GM storage (sobrevive recargas) -------------------
  let buffer = GM_getValue('saleshub.buffer', []);
  function saveBuffer() { GM_setValue('saleshub.buffer', buffer); updatePanel(); }

  // -------- Extractores -------------------------------------------------
  // Lee el panel de detalle abierto (cuando el vendedor clickeó un negocio).
  function readDetailPanel() {
    // Hay un <main aria-label="<nombre>"> cuando un place está abierto.
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

    // Rating + reviews aparecen como "4,9" + "(123)" cerca del título.
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

    // Tipo de negocio: "Centro de yoga", "Gimnasio"… está como botón cerca del título.
    const type = main.querySelector('button[jsaction*="category"]')?.innerText?.trim() || null;

    // Lat/lng del URL actual: /place/Name/@lat,lng,zoom/...
    const latlng = location.href.match(/@(-?\d+\.\d+),(-?\d+\.\d+)/);
    const lat = latlng ? parseFloat(latlng[1]) : null;
    const lng = latlng ? parseFloat(latlng[2]) : null;

    return {
      name, phone, address, website, rating, totalReviews: reviews, type,
      businessStatus: null, latitude: lat, longitude: lng
    };
  }

  // Busca un elemento con aria-label que matchee algún regex y devuelve el grupo 1.
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

  // Captura todos los items visibles en el feed lateral.
  function readFeedList() {
    const articles = document.querySelectorAll('div[role="feed"] > div > a[href*="/maps/place/"]');
    const out = [];
    articles.forEach(a => {
      const card = a.closest('div[role="article"]') || a.parentElement;
      // El nombre suele ser el texto del link.
      const name = a.getAttribute('aria-label') || card?.querySelector('div[role="heading"]')?.innerText?.trim();
      if (!name) return;
      const text = (card?.innerText || '').replace(/\s+/g, ' ').trim();
      // Teléfono: heurística de "+54..." o "011 ..." en el texto.
      const phoneM = text.match(/(\+?\d[\d\s\-]{7,}\d)/);
      const phone = phoneM ? phoneM[1].trim() : null;
      const ratingM = text.match(/(\d+[\.,]\d+)\s*\((\d[\d\.,]*)\)/);
      const rating = ratingM ? parseFloat(ratingM[1].replace(',', '.')) : null;
      const reviews = ratingM ? parseInt(ratingM[2].replace(/[\.,]/g, ''), 10) : null;
      // Tipo + dirección suelen estar separados por " · ".
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
    // Dedup local por (name + phone)
    const key = `${item.name}|${item.phone || ''}`;
    if (buffer.some(b => `${b.name}|${b.phone || ''}` === key)) return false;
    buffer.push(item);
    saveBuffer();
    return true;
  }

  // -------- UI flotante -------------------------------------------------
  const panel = document.createElement('div');
  panel.id = 'saleshub-panel';
  panel.style.cssText = `
    position: fixed; bottom: 16px; right: 16px; z-index: 99999;
    background: white; border: 1px solid #cbd5e1; border-radius: 8px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.15); padding: 10px 12px;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; font-size: 12px;
    width: 280px;
  `;
  document.body.appendChild(panel);

  function updatePanel() {
    const has = !!cfg.token && !!cfg.productKey;
    panel.innerHTML = `
      <div style="font-weight:600;color:#0f172a;margin-bottom:6px;">SalesHub · captura</div>
      ${!has ? `<div style="color:#dc2626;margin-bottom:6px;">Falta configurar token + producto. Click "Config".</div>` : ''}
      <div style="color:#475569;margin-bottom:6px;">
        Buffer: <b>${buffer.length}</b> items
        ${cfg.productKey ? ` · <code>${cfg.productKey}</code>` : ''}
        ${cfg.localityGid2 ? ` · ${cfg.localityGid2}` : ''}
      </div>
      <div style="display:flex;gap:6px;flex-wrap:wrap;">
        <button id="sh-add-detail" style="${btnStyle('#2563eb')}">+ Este lugar</button>
        <button id="sh-add-list" style="${btnStyle('#0891b2')}">+ Listado visible</button>
        <button id="sh-upload" style="${btnStyle('#16a34a')}" ${buffer.length === 0 || !has ? 'disabled' : ''}>
          Subir ${buffer.length}
        </button>
        <button id="sh-clear" style="${btnStyle('#94a3b8')}" ${buffer.length === 0 ? 'disabled' : ''}>Vaciar</button>
        <button id="sh-cfg" style="${btnStyle('#64748b')}">Config</button>
      </div>
    `;
    panel.querySelector('#sh-add-detail').onclick = () => {
      const it = readDetailPanel();
      if (!it) return toast('No detecté un panel de detalle abierto. Click un negocio primero.');
      const added = addToBuffer(it);
      toast(added ? `+ ${it.name}` : `Ya estaba: ${it.name}`);
    };
    panel.querySelector('#sh-add-list').onclick = () => {
      const list = readFeedList();
      if (list.length === 0) return toast('No detecté un listado lateral con resultados.');
      let added = 0;
      list.forEach(it => { if (addToBuffer(it)) added++; });
      toast(`+ ${added} (de ${list.length} en el listado)`);
    };
    panel.querySelector('#sh-upload').onclick = upload;
    panel.querySelector('#sh-clear').onclick = () => { buffer = []; saveBuffer(); toast('Buffer vacío'); };
    panel.querySelector('#sh-cfg').onclick = openConfig;
  }
  updatePanel();

  function btnStyle(bg) {
    return `background:${bg};color:white;border:0;padding:5px 9px;border-radius:5px;cursor:pointer;font-size:11px;`;
  }

  function toast(msg) {
    const t = document.createElement('div');
    t.textContent = msg;
    t.style.cssText = `
      position: fixed; bottom: 80px; right: 16px; z-index: 100000;
      background: #0f172a; color: white; padding: 8px 12px; border-radius: 6px;
      font-family: -apple-system, sans-serif; font-size: 12px; max-width: 260px;
    `;
    document.body.appendChild(t);
    setTimeout(() => t.remove(), 2200);
  }

  function openConfig() {
    const api = prompt('URL del backend SalesHub:', cfg.api);
    if (api == null) return;
    const token = prompt('JWT (copialo desde el dashboard de SalesHub):', cfg.token);
    if (token == null) return;
    const product = prompt('productKey por defecto (ej. gymhero):', cfg.productKey);
    if (product == null) return;
    cfg.api = api.replace(/\/$/, '');
    cfg.token = token;
    cfg.productKey = product;
    GM_setValue('saleshub.api', cfg.api);
    GM_setValue('saleshub.token', cfg.token);
    GM_setValue('saleshub.productKey', cfg.productKey);
    toast('Config guardada');
    updatePanel();
  }

  function upload() {
    if (buffer.length === 0) return;
    if (!cfg.token || !cfg.productKey) return toast('Falta config');
    const body = {
      productKey: cfg.productKey,
      localityGid2: cfg.localityGid2 || null,
      category: cfg.category || null,
      query: getCurrentQuery(),
      items: buffer
    };
    GM_xmlhttpRequest({
      method: 'POST',
      url: `${cfg.api}/api/search-jobs`,
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${cfg.token}`
      },
      data: JSON.stringify(body),
      onload: (res) => {
        if (res.status >= 200 && res.status < 300) {
          let parsed = {}; try { parsed = JSON.parse(res.responseText); } catch {}
          toast(`✓ ${parsed.leadsCreated ?? 0} nuevos · ${parsed.duplicates ?? 0} dup · ${parsed.skipped ?? 0} skip`);
          buffer = []; saveBuffer();
        } else {
          toast(`Error ${res.status}: ${res.responseText.slice(0, 100)}`);
        }
      },
      onerror: () => toast('Error de red al subir')
    });
  }

  function getCurrentQuery() {
    // /maps/search/<query>/...
    const m = location.pathname.match(/\/maps\/search\/([^/]+)/);
    if (m) return decodeURIComponent(m[1]).replace(/\+/g, ' ');
    return '(captura ad-hoc)';
  }

  // Re-render cuando cambia el buffer en otra pestaña.
  setInterval(() => {
    const fresh = GM_getValue('saleshub.buffer', []);
    if (fresh.length !== buffer.length) { buffer = fresh; updatePanel(); }
  }, 2000);
})();
