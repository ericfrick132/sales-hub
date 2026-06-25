/**
 * Editor de n8n embebido. n8n vive en el mismo droplet (n8n.efcloud.tech) y es
 * same-site con sales.efcloud.tech, así que el iframe carga con la sesión de n8n.
 * Caddy permite el frame solo desde sales.efcloud.tech (frame-ancestors).
 */
export default function Automatizaciones() {
  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h1 className="text-xl md:text-2xl font-bold">Automatizaciones (n8n)</h1>
          <p className="text-xs text-slate-500">
            Editor visual embebido. Si te pide login, entrá una vez con tu usuario de n8n.
          </p>
        </div>
        <a
          href="https://n8n.efcloud.tech"
          target="_blank"
          rel="noreferrer"
          className="btn-secondary text-xs whitespace-nowrap"
        >
          Abrir en pestaña ↗
        </a>
      </div>
      <div className="card p-0 overflow-hidden">
        <iframe
          src="https://n8n.efcloud.tech"
          title="n8n"
          className="w-full block"
          style={{ height: 'calc(100vh - 11rem)', border: 0 }}
        />
      </div>
    </div>
  );
}
