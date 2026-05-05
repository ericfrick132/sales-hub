// Cache persistente cliente para archivos pesados (geojson de localidades).
// Usa la Cache API del browser, que sobrevive a recargas y no toca la cuota
// chica de localStorage. Bumpear CACHE_VERSION cuando regeneramos el dataset.
const CACHE_VERSION = 'v1';
const CACHE_NAME = `saleshub-static-${CACHE_VERSION}`;

export async function fetchCachedJson<T>(url: string): Promise<T> {
  // Fallback si el browser no soporta Cache API (ej. Safari modo privado viejo).
  if (typeof caches === 'undefined') {
    const r = await fetch(url);
    if (!r.ok) throw new Error(`status ${r.status}`);
    return r.json() as Promise<T>;
  }

  const cache = await caches.open(CACHE_NAME);
  const cached = await cache.match(url);
  if (cached) {
    return cached.json() as Promise<T>;
  }

  const res = await fetch(url);
  if (!res.ok) throw new Error(`status ${res.status}`);
  // Clonamos antes de leer: el body se consume una sola vez.
  const clone = res.clone();
  const json = (await res.json()) as T;
  // Persistir es best-effort: si el browser rechaza por cuota (geojsons grandes
  // pueden pasar varios MB) preferimos seguir y devolver el JSON ya parseado
  // antes que romper el componente entero.
  try {
    await cache.put(url, clone);
  } catch (err) {
    console.warn('No se pudo cachear', url, err);
  }
  return json;
}
