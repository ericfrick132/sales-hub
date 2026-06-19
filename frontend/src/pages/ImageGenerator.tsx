import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

interface IgAccount {
  id: string;
  username: string;
  isLoggedIn: boolean;
  isActionBlocked: boolean;
}

interface GeneratedImage {
  prompt: string;
  url: string;
  timestamp: Date;
}

const ASPECT_RATIOS = [
  { label: 'Cuadrado (1:1)', width: 1080, height: 1080 },
  { label: 'Portrait (4:5)', width: 1080, height: 1350 },
  { label: 'Story (9:16)', width: 1080, height: 1920 },
  { label: 'Landscape (1.91:1)', width: 1200, height: 628 },
];

const ESTILOS_RAPIDOS = [
  'fotografía profesional, luz natural, alta calidad',
  'minimalista, fondo blanco, producto destacado',
  'vibrante, colorido, redes sociales',
  'oscuro y elegante, iluminación dramática',
  'flat lay, vista desde arriba, estético',
  'lifestyle, personas reales, natural',
];

function buildUrl(prompt: string, width: number, height: number, seed: number) {
  return `https://image.pollinations.ai/prompt/${encodeURIComponent(prompt)}?width=${width}&height=${height}&seed=${seed}&nologo=true&enhance=true`;
}

export default function ImageGenerator() {
  const [prompt, setPrompt] = useState('');
  const [selectedRatio, setSelectedRatio] = useState(0);
  const [loading, setLoading] = useState(false);
  const [images, setImages] = useState<GeneratedImage[]>([]);
  const [variations, setVariations] = useState(1);
  const [lightbox, setLightbox] = useState<string | null>(null);
  const [selectedAccountId, setSelectedAccountId] = useState('');
  const [caption, setCaption] = useState('');
  const [posting, setPosting] = useState<string | null>(null);

  const ratio = ASPECT_RATIOS[selectedRatio];

  const accountsQ = useQuery({
    queryKey: ['ig-accounts'],
    queryFn: async () => (await api.get<IgAccount[]>('/instagram-accounts')).data
  });

  const accounts = (accountsQ.data ?? []).filter(a => a.isLoggedIn && !a.isActionBlocked);

  const generar = async () => {
    if (!prompt.trim()) {
      toast.error('Escribí un prompt primero');
      return;
    }
    setLoading(true);
    const toastId = toast.loading(`Generando ${variations} imagen${variations > 1 ? 'es' : ''}…`);

    try {
      const nuevas: GeneratedImage[] = Array.from({ length: variations }, (_, i) => ({
        prompt: prompt.trim(),
        url: buildUrl(prompt.trim(), ratio.width, ratio.height, Date.now() + i * 1000),
        timestamp: new Date(),
      }));

      await Promise.all(
        nuevas.map(
          (img) =>
            new Promise<void>((resolve, reject) => {
              const el = new Image();
              el.onload = () => resolve();
              el.onerror = () => reject();
              el.src = img.url;
            })
        )
      );

      setImages((prev) => [...nuevas, ...prev]);
      toast.success(`${variations} imagen${variations > 1 ? 'es' : ''} lista${variations > 1 ? 's' : ''}`, { id: toastId });
    } catch {
      toast.error('Error generando imágenes. Reintentá.', { id: toastId });
    } finally {
      setLoading(false);
    }
  };

  const postear = async (imageUrl: string) => {
    if (!selectedAccountId) {
      toast.error('Elegí una cuenta de Instagram primero');
      return;
    }
    setPosting(imageUrl);
    const toastId = toast.loading('Posteando en Instagram…');
    try {
      await api.post(`/instagram-accounts/${selectedAccountId}/post`, {
        imageUrl,
        caption
      });
      toast.success('¡Post publicado!', { id: toastId });
    } catch (e: any) {
      toast.error(e.response?.data?.error ?? 'Error al postear', { id: toastId });
    } finally {
      setPosting(null);
    }
  };

  const descargar = async (url: string, prompt: string) => {
    try {
      const res = await fetch(url);
      const blob = await res.blob();
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = `${prompt.slice(0, 40).replace(/\s+/g, '_')}.jpg`;
      a.click();
      toast.success('Descargando…');
    } catch {
      toast.error('No se pudo descargar');
    }
  };

  const copiarPrompt = (p: string) => {
    navigator.clipboard.writeText(p);
    toast.success('Prompt copiado');
  };

  return (
    <div className="space-y-6">
      <header>
        <h1 className="text-2xl font-bold">Generador de imágenes</h1>
        <p className="text-sm text-slate-500 mt-1">
          Generá imágenes con IA para tus cuentas de Instagram. Gratis, sin límites, powered by Pollinations.
        </p>
      </header>

      <section className="card p-4 space-y-4">
        <h2 className="font-semibold">Nuevo prompt</h2>

        <div>
          <label className="text-xs text-slate-500">Prompt</label>
          <textarea
            className="input resize-none"
            rows={3}
            placeholder="ej: cancha de pádel profesional, luz de estadio, fotografía deportiva"
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter' && e.metaKey) generar(); }}
          />
        </div>

        <div>
          <label className="text-xs text-slate-500 mb-1 block">Agregar estilo rápido</label>
          <div className="flex flex-wrap gap-2">
            {ESTILOS_RAPIDOS.map((e) => (
              <button
                key={e}
                className="text-xs px-2 py-1 rounded border border-slate-200 hover:bg-slate-50 text-slate-600"
                onClick={() => setPrompt((p) => p ? `${p}, ${e}` : e)}>
                + {e.split(',')[0]}
              </button>
            ))}
          </div>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <div>
            <label className="text-xs text-slate-500">Formato</label>
            <select className="input" value={selectedRatio} onChange={(e) => setSelectedRatio(Number(e.target.value))}>
              {ASPECT_RATIOS.map((r, i) => (
                <option key={r.label} value={i}>{r.label}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500">Variaciones</label>
            <select className="input" value={variations} onChange={(e) => setVariations(Number(e.target.value))}>
              {[1, 2, 3, 4].map((n) => (
                <option key={n} value={n}>{n} imagen{n > 1 ? 'es' : ''}</option>
              ))}
            </select>
          </div>
          <div className="flex items-end">
            <button className="btn-primary w-full" disabled={loading || !prompt.trim()} onClick={generar}>
              {loading ? 'Generando…' : 'Generar ✦'}
            </button>
          </div>
        </div>

        <div className="border-t border-slate-100 pt-4 space-y-3">
          <h3 className="text-sm font-semibold">Postear en Instagram</h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="text-xs text-slate-500">Cuenta</label>
              <select className="input" value={selectedAccountId} onChange={(e) => setSelectedAccountId(e.target.value)}>
                <option value="">— elegir cuenta —</option>
                {accounts.map((a) => (
                  <option key={a.id} value={a.id}>@{a.username}</option>
                ))}
              </select>
            </div>
            <div>
              <label className="text-xs text-slate-500">Caption (opcional)</label>
              <input
                className="input"
                placeholder="Texto del post…"
                value={caption}
                onChange={(e) => setCaption(e.target.value)}
              />
            </div>
          </div>
        </div>

        <p className="text-xs text-slate-400">⌘ + Enter para generar rápido</p>
      </section>

      {loading && (
        <div className={`grid gap-3 ${variations > 1 ? 'grid-cols-2' : 'grid-cols-1 max-w-sm'}`}>
          {Array.from({ length: variations }).map((_, i) => (
            <div key={i} className="rounded-lg bg-slate-100 animate-pulse" style={{ aspectRatio: `${ratio.width}/${ratio.height}` }} />
          ))}
        </div>
      )}

      {images.length > 0 && (
        <section className="space-y-3">
          <div className="flex items-center justify-between">
            <h2 className="font-semibold">Generadas ({images.length})</h2>
            <button className="text-xs text-slate-400 hover:text-rose-600" onClick={() => { if (confirm('¿Limpiar toda la galería?')) setImages([]); }}>
              Limpiar todo
            </button>
          </div>

          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-3">
            {images.map((img, i) => (
              <div key={i} className="card overflow-hidden">
                <img
                  src={img.url}
                  alt={img.prompt}
                  className="w-full object-cover cursor-zoom-in"
                  style={{ aspectRatio: '1/1' }}
                  onClick={() => setLightbox(img.url)}
                />
                <div className="p-2 space-y-1">
                  <p className="text-xs text-slate-500 truncate" title={img.prompt}>{img.prompt}</p>
                  <p className="text-[10px] text-slate-400">{img.timestamp.toLocaleTimeString()}</p>
                  <div className="flex gap-1">
                    <button className="flex-1 text-[11px] px-2 py-1 rounded border border-slate-200 hover:bg-slate-50" onClick={() => descargar(img.url, img.prompt)}>
                      Descargar
                    </button>
                    <button className="text-[11px] px-2 py-1 rounded border border-slate-200 hover:bg-slate-50" onClick={() => copiarPrompt(img.prompt)}>
                      📋
                    </button>
                    <button className="text-[11px] px-2 py-1 rounded border border-slate-200 hover:bg-slate-50" onClick={() => { setPrompt(img.prompt); window.scrollTo({ top: 0, behavior: 'smooth' }); }}>
                      ↻
                    </button>
                  </div>
                  <button
                    className="w-full text-[11px] px-2 py-1 rounded border border-emerald-300 text-emerald-700 hover:bg-emerald-50 disabled:opacity-50"
                    disabled={posting === img.url || !selectedAccountId}
                    onClick={() => postear(img.url)}>
                    {posting === img.url ? 'Posteando…' : '📤 Postear en IG'}
                  </button>
                </div>
              </div>
            ))}
          </div>
        </section>
      )}

      {lightbox && (
        <div className="fixed inset-0 z-50 bg-black/80 flex items-center justify-center p-4" onClick={() => setLightbox(null)}>
          <img src={lightbox} alt="preview" className="max-w-full max-h-full rounded-lg shadow-2xl" onClick={(e) => e.stopPropagation()} />
          <button className="absolute top-4 right-4 text-white text-2xl font-bold" onClick={() => setLightbox(null)}>✕</button>
        </div>
      )}
    </div>
  );
}
