// Corre en sales-hub: si hay un admin logueado, trae la clave de la extensión con su sesión y la
// guarda. Así nadie copia ni pega claves; alcanza con abrir sales-hub una vez.
(async () => {
  try {
    const auth = JSON.parse(localStorage.getItem('saleshub-auth') || '{}').state;
    if (!auth?.token || auth.user?.role !== 'Admin') return;
    const r = await fetch('https://api.sales.efcloud.tech/api/audience/ingest-key', { headers: { Authorization: `Bearer ${auth.token}` } });
    if (!r.ok) return;
    const { key } = await r.json();
    const { apiKey } = await chrome.storage.local.get('apiKey');
    if (key && key !== apiKey) await chrome.storage.local.set({ apiKey: key, lastError: '' });
  } catch { /* sin sesión o sin red: se intenta la próxima vez que se abra sales-hub */ }
})();
