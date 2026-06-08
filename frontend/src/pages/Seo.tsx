import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type {
  SeoSite, SeoKeyword, SeoArticleSummary, SeoArticleFull,
  SeoContentType, SeoArticleStatus, SeoKeywordStatus
} from '../lib/types';

const CONTENT_TYPES: { value: SeoContentType; label: string }[] = [
  { value: 'Article', label: 'Artículo' },
  { value: 'Guide', label: 'Guía / Pillar' },
  { value: 'Faq', label: 'FAQ (GEO)' },
  { value: 'Comparison', label: 'Comparativa' },
  { value: 'Landing', label: 'Landing' }
];

const ARTICLE_STATUS_LABEL: Record<SeoArticleStatus, string> = {
  Draft: 'Borrador',
  NeedsReview: 'Para revisar',
  Approved: 'Aprobado',
  Published: 'Publicado',
  Archived: 'Archivado'
};

export default function Seo() {
  const qc = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [editingSite, setEditingSite] = useState<SeoSite | null>(null);
  const [creatingSite, setCreatingSite] = useState(false);

  const sitesQ = useQuery({
    queryKey: ['seo-sites'],
    queryFn: async () => (await api.get<SeoSite[]>('/seo/sites')).data,
    refetchInterval: 30000
  });

  const seedMut = useMutation({
    mutationFn: async () => (await api.post('/seo/sites/seed-apps')).data,
    onSuccess: (d: any) => {
      toast.success(d.message ?? 'Apps creadas');
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const createMut = useMutation({
    mutationFn: async (body: Record<string, unknown>) => (await api.post('/seo/sites', body)).data,
    onSuccess: () => {
      toast.success('Sitio creado');
      setCreatingSite(false);
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const updateMut = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: Record<string, unknown> }) =>
      (await api.put(`/seo/sites/${id}`, body)).data,
    onSuccess: () => {
      toast.success('Sitio actualizado');
      setEditingSite(null);
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const deleteMut = useMutation({
    mutationFn: async (id: string) => (await api.delete(`/seo/sites/${id}`)).data,
    onSuccess: () => {
      toast.success('Sitio dado de baja');
      if (selectedId) setSelectedId(null);
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
    }
  });

  const sites = sitesQ.data ?? [];
  const selected = sites.find((s) => s.id === selectedId) ?? null;

  return (
    <div className="space-y-6">
      <header className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-2xl font-bold">SEO / Contenido</h1>
          <p className="text-sm text-slate-500 mt-1">
            Motor propio de SEO + GEO: investigá keywords reales, generá artículos optimizados con FAQ y JSON-LD,
            revisá y exportá a tu CMS. Un sitio por app.
          </p>
        </div>
        <div className="flex gap-2">
          <button
            className="btn-secondary text-xs"
            disabled={seedMut.isPending}
            onClick={() => seedMut.mutate()}>
            {seedMut.isPending ? 'Creando…' : 'Crear las 6 apps'}
          </button>
          <button className="btn-primary text-xs" onClick={() => setCreatingSite(true)}>
            + Nuevo sitio
          </button>
        </div>
      </header>

      <div className="grid grid-cols-1 lg:grid-cols-[20rem_1fr] gap-6">
        {/* Lista de sitios */}
        <section className="space-y-2">
          {sites.length === 0 && (
            <div className="card p-4 text-sm text-slate-500">
              No hay sitios. Tocá "Crear las 6 apps" para arrancar con GymHero, TurnosPro, etc.
            </div>
          )}
          {sites.map((s) => (
            <button
              key={s.id}
              onClick={() => setSelectedId(s.id)}
              className={`w-full text-left card p-3 transition ${
                selectedId === s.id ? 'ring-2 ring-brand-600' : 'hover:bg-slate-50'
              } ${!s.isActive ? 'opacity-50' : ''}`}>
              <div className="flex items-center justify-between">
                <div className="font-semibold">{s.name}</div>
                {s.needsReviewCount > 0 && (
                  <span className="badge bg-amber-500 text-white text-xs">{s.needsReviewCount} a revisar</span>
                )}
              </div>
              <div className="text-xs text-slate-500 mt-0.5">{s.domain || 'sin dominio'}</div>
              <div className="text-xs text-slate-400 mt-1">
                {s.keywordCount} keywords · {s.articleCount} artículos
                {s.autoPublish && <span className="text-emerald-600"> · agente 24/7</span>}
              </div>
            </button>
          ))}
        </section>

        {/* Detalle del sitio seleccionado */}
        <section>
          {!selected ? (
            <div className="card p-8 text-center text-sm text-slate-400">
              Elegí un sitio para gestionar sus keywords y contenido.
            </div>
          ) : (
            <SiteDetail
              site={selected}
              onEdit={() => setEditingSite(selected)}
              onDelete={() => {
                if (confirm(`¿Dar de baja ${selected.name}? No se borra el contenido, solo se desactiva.`))
                  deleteMut.mutate(selected.id);
              }}
            />
          )}
        </section>
      </div>

      {(creatingSite || editingSite) && (
        <SiteForm
          site={editingSite}
          submitting={createMut.isPending || updateMut.isPending}
          onCancel={() => {
            setCreatingSite(false);
            setEditingSite(null);
          }}
          onSubmit={(body) => {
            if (editingSite) updateMut.mutate({ id: editingSite.id, body });
            else createMut.mutate(body);
          }}
        />
      )}
    </div>
  );
}

// ============================================================== SITE DETAIL

function SiteDetail({ site, onEdit, onDelete }: { site: SeoSite; onEdit: () => void; onDelete: () => void }) {
  const [tab, setTab] = useState<'keywords' | 'articles'>('keywords');

  return (
    <div className="space-y-4">
      <div className="card p-4">
        <div className="flex items-start justify-between gap-3 flex-wrap">
          <div>
            <h2 className="text-xl font-bold">{site.name}</h2>
            <div className="text-sm text-slate-500">{site.sector || <em className="text-amber-600">falta definir el nicho</em>}</div>
            {site.targetCountries.length > 0 && (
              <div className="text-xs text-slate-400 mt-1">Mercados: {site.targetCountries.join(', ')}</div>
            )}
          </div>
          <div className="flex gap-2">
            <button className="text-xs px-2 py-1 rounded border border-slate-300 hover:bg-slate-50" onClick={onEdit}>
              Editar sitio
            </button>
            <button className="text-xs px-2 py-1 rounded border border-rose-300 text-rose-700 hover:bg-rose-50" onClick={onDelete}>
              Baja
            </button>
          </div>
        </div>
      </div>

      <div className="flex gap-2 border-b border-slate-200">
        <Tab active={tab === 'keywords'} onClick={() => setTab('keywords')}>Keywords</Tab>
        <Tab active={tab === 'articles'} onClick={() => setTab('articles')}>Artículos</Tab>
      </div>

      {tab === 'keywords' ? <KeywordsPanel site={site} /> : <ArticlesPanel site={site} />}
    </div>
  );
}

function Tab({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={`px-4 py-2 text-sm -mb-px border-b-2 ${
        active ? 'border-brand-600 text-brand-600 font-medium' : 'border-transparent text-slate-500 hover:text-slate-700'
      }`}>
      {children}
    </button>
  );
}

// ============================================================== KEYWORDS

function KeywordsPanel({ site }: { site: SeoSite }) {
  const qc = useQueryClient();
  const [seed, setSeed] = useState(site.sector || '');
  const [genFor, setGenFor] = useState<SeoKeyword | null>(null);

  const kwQ = useQuery({
    queryKey: ['seo-keywords', site.id],
    queryFn: async () => (await api.get<SeoKeyword[]>(`/seo/sites/${site.id}/keywords`)).data
  });

  const researchMut = useMutation({
    mutationFn: async (seedTopic: string) =>
      (await api.post(`/seo/sites/${site.id}/keywords/research`, { seedTopic })).data,
    onSuccess: (d: any) => {
      toast.success(`${d.count} keywords nuevas`);
      qc.invalidateQueries({ queryKey: ['seo-keywords', site.id] });
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló la investigación')
  });

  const patchMut = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: Record<string, unknown> }) =>
      (await api.patch(`/seo/keywords/${id}`, body)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['seo-keywords', site.id] })
  });

  const deleteMut = useMutation({
    mutationFn: async (id: string) => (await api.delete(`/seo/keywords/${id}`)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['seo-keywords', site.id] });
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
    }
  });

  const keywords = kwQ.data ?? [];

  return (
    <div className="space-y-4">
      <div className="card p-4 space-y-3">
        <h3 className="font-semibold text-sm">Investigar keywords</h3>
        <p className="text-xs text-slate-500">
          Combina sugerencias reales de Google (lo que la gente busca) con clusterización por IA: intención,
          prioridad y preguntas para GEO.
        </p>
        <div className="flex gap-2">
          <input
            className="input flex-1"
            value={seed}
            onChange={(e) => setSeed(e.target.value)}
            placeholder="tema semilla (ej. software para gimnasios)"
          />
          <button
            className="btn-primary text-xs whitespace-nowrap"
            disabled={!seed.trim() || researchMut.isPending}
            onClick={() => researchMut.mutate(seed.trim())}>
            {researchMut.isPending ? 'Investigando…' : 'Investigar'}
          </button>
        </div>
      </div>

      <div className="card divide-y divide-slate-100">
        {keywords.length === 0 && (
          <div className="p-4 text-sm text-slate-500">Sin keywords. Corré una investigación arriba.</div>
        )}
        {keywords.map((k) => (
          <div key={k.id} className="p-3 flex flex-wrap items-center gap-3">
            <div className="flex-1 min-w-[14rem]">
              <div className="font-medium text-sm">{k.term}</div>
              <div className="text-xs text-slate-400">
                {k.cluster && <>cluster: {k.cluster} · </>}
                {k.source}
                {k.volume != null && <> · vol {k.volume}</>}
              </div>
            </div>
            <IntentBadge intent={k.intent} />
            <span className="text-xs text-slate-500" title="prioridad">★ {k.priority}</span>
            <KeywordStatusSelect
              value={k.status}
              onChange={(status) => patchMut.mutate({ id: k.id, body: { status } })}
            />
            <div className="flex gap-1">
              <button
                className="text-xs px-2 py-1 rounded border border-brand-300 text-brand-700 hover:bg-brand-50"
                onClick={() => setGenFor(k)}>
                Generar
              </button>
              <button
                className="text-xs px-2 py-1 rounded border border-rose-300 text-rose-700 hover:bg-rose-50"
                onClick={() => deleteMut.mutate(k.id)}>
                ×
              </button>
            </div>
          </div>
        ))}
      </div>

      {genFor && (
        <GenerateModal
          site={site}
          keyword={genFor}
          onClose={() => setGenFor(null)}
        />
      )}
    </div>
  );
}

function IntentBadge({ intent }: { intent: SeoKeyword['intent'] }) {
  const map: Record<string, { c: string; l: string }> = {
    Informational: { c: 'bg-sky-100 text-sky-700', l: 'info' },
    Commercial: { c: 'bg-violet-100 text-violet-700', l: 'comercial' },
    Transactional: { c: 'bg-emerald-100 text-emerald-700', l: 'transacc.' },
    Navigational: { c: 'bg-slate-200 text-slate-600', l: 'navega.' }
  };
  const m = map[intent] ?? map.Informational;
  return <span className={`text-[10px] px-2 py-0.5 rounded ${m.c}`}>{m.l}</span>;
}

function KeywordStatusSelect({ value, onChange }: { value: SeoKeywordStatus; onChange: (v: SeoKeywordStatus) => void }) {
  return (
    <select
      className="text-xs border border-slate-200 rounded px-1 py-0.5 bg-white"
      value={value}
      onChange={(e) => onChange(e.target.value as SeoKeywordStatus)}>
      <option value="Idea">idea</option>
      <option value="Planned">planificada</option>
      <option value="Used">usada</option>
      <option value="Ignored">ignorada</option>
    </select>
  );
}

// ============================================================== ARTICLES

function ArticlesPanel({ site }: { site: SeoSite }) {
  const qc = useQueryClient();
  const [openId, setOpenId] = useState<string | null>(null);
  const [showGen, setShowGen] = useState(false);

  const artQ = useQuery({
    queryKey: ['seo-articles', site.id],
    queryFn: async () => (await api.get<SeoArticleSummary[]>(`/seo/sites/${site.id}/articles`)).data
  });

  const deleteMut = useMutation({
    mutationFn: async (id: string) => (await api.delete(`/seo/articles/${id}`)).data,
    onSuccess: () => {
      toast.success('Artículo eliminado');
      qc.invalidateQueries({ queryKey: ['seo-articles', site.id] });
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
    }
  });

  const articles = artQ.data ?? [];

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button className="btn-primary text-xs" onClick={() => setShowGen(true)}>+ Generar artículo</button>
      </div>

      <div className="card divide-y divide-slate-100">
        {articles.length === 0 && (
          <div className="p-4 text-sm text-slate-500">Sin artículos. Generá uno desde una keyword o con "+ Generar".</div>
        )}
        {articles.map((a) => (
          <div key={a.id} className="p-3 flex flex-wrap items-center gap-3">
            <button className="flex-1 min-w-[16rem] text-left" onClick={() => setOpenId(a.id)}>
              <div className="font-medium text-sm hover:text-brand-600">{a.title}</div>
              <div className="text-xs text-slate-400">
                {a.targetKeyword} · {a.wordCount} palabras · {new Date(a.updatedAt).toLocaleDateString()}
              </div>
            </button>
            {a.seoScore != null && (
              <span className="text-xs text-slate-500" title="SEO score">{a.seoScore}/100</span>
            )}
            <ArticleStatusBadge status={a.status} />
            <button
              className="text-xs px-2 py-1 rounded border border-rose-300 text-rose-700 hover:bg-rose-50"
              onClick={() => {
                if (confirm('¿Eliminar artículo?')) deleteMut.mutate(a.id);
              }}>
              ×
            </button>
          </div>
        ))}
      </div>

      {showGen && <GenerateModal site={site} keyword={null} onClose={() => setShowGen(false)} />}
      {openId && <ArticleModal articleId={openId} siteId={site.id} onClose={() => setOpenId(null)} />}
    </div>
  );
}

function ArticleStatusBadge({ status }: { status: SeoArticleStatus }) {
  const map: Record<SeoArticleStatus, string> = {
    Draft: 'bg-slate-200 text-slate-600',
    NeedsReview: 'bg-amber-100 text-amber-700',
    Approved: 'bg-sky-100 text-sky-700',
    Published: 'bg-emerald-100 text-emerald-700',
    Archived: 'bg-slate-100 text-slate-400'
  };
  return <span className={`text-[10px] px-2 py-0.5 rounded ${map[status]}`}>{ARTICLE_STATUS_LABEL[status]}</span>;
}

// ============================================================== GENERATE MODAL

function GenerateModal({ site, keyword, onClose }: { site: SeoSite; keyword: SeoKeyword | null; onClose: () => void }) {
  const qc = useQueryClient();
  const [kw, setKw] = useState(keyword?.term ?? '');
  const [type, setType] = useState<SeoContentType>('Article');

  const genMut = useMutation({
    mutationFn: async () =>
      (await api.post<SeoArticleFull>(`/seo/sites/${site.id}/articles/generate`, {
        keyword: kw.trim(),
        contentType: type,
        keywordId: keyword?.id ?? null
      })).data,
    onSuccess: () => {
      toast.success('Artículo generado (queda para revisar)');
      qc.invalidateQueries({ queryKey: ['seo-articles', site.id] });
      qc.invalidateQueries({ queryKey: ['seo-keywords', site.id] });
      qc.invalidateQueries({ queryKey: ['seo-sites'] });
      onClose();
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló la generación')
  });

  return (
    <Modal title="Generar artículo" onCancel={onClose} wide={false}>
      <div className="space-y-3">
        <Field label="Keyword objetivo">
          <input className="input" value={kw} onChange={(e) => setKw(e.target.value)} />
        </Field>
        <Field label="Tipo de contenido">
          <select className="input" value={type} onChange={(e) => setType(e.target.value as SeoContentType)}>
            {CONTENT_TYPES.map((t) => (
              <option key={t.value} value={t.value}>{t.label}</option>
            ))}
          </select>
        </Field>
        <p className="text-xs text-slate-500">
          Genera título, meta, cuerpo en Markdown, FAQ y JSON-LD (schema.org). Puede tardar ~30-60s. Queda en
          "Para revisar": no se publica nada solo.
        </p>
      </div>
      <div className="flex justify-end gap-2 mt-4">
        <button className="text-xs px-3 py-1 rounded border border-slate-300" onClick={onClose}>Cancelar</button>
        <button
          className="btn-primary"
          disabled={!kw.trim() || genMut.isPending}
          onClick={() => genMut.mutate()}>
          {genMut.isPending ? 'Generando…' : 'Generar'}
        </button>
      </div>
    </Modal>
  );
}

// ============================================================== ARTICLE MODAL

function ArticleModal({ articleId, siteId, onClose }: { articleId: string; siteId: string; onClose: () => void }) {
  const qc = useQueryClient();
  const artQ = useQuery({
    queryKey: ['seo-article', articleId],
    queryFn: async () => (await api.get<SeoArticleFull>(`/seo/articles/${articleId}`)).data
  });

  const [title, setTitle] = useState('');
  const [meta, setMeta] = useState('');
  const [body, setBody] = useState('');
  const [dirty, setDirty] = useState(false);
  const [loaded, setLoaded] = useState(false);

  const a = artQ.data;
  if (a && !loaded) {
    setTitle(a.title);
    setMeta(a.metaDescription);
    setBody(a.bodyMarkdown);
    setLoaded(true);
  }

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['seo-article', articleId] });
    qc.invalidateQueries({ queryKey: ['seo-articles', siteId] });
    qc.invalidateQueries({ queryKey: ['seo-sites'] });
  };

  const patchMut = useMutation({
    mutationFn: async (body: Record<string, unknown>) =>
      (await api.patch<SeoArticleFull>(`/seo/articles/${articleId}`, body)).data,
    onSuccess: () => { toast.success('Guardado'); setDirty(false); invalidate(); },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const optimizeMut = useMutation({
    mutationFn: async () => (await api.post<SeoArticleFull>(`/seo/articles/${articleId}/optimize`)).data,
    onSuccess: () => { toast.success('Optimización lista'); invalidate(); },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const setStatus = (status: SeoArticleStatus) => patchMut.mutate({ status });
  const save = () => patchMut.mutate({ title, metaDescription: meta, bodyMarkdown: body });
  const copy = (text: string, what: string) => {
    navigator.clipboard.writeText(text).then(() => toast.success(`${what} copiado`));
  };

  return (
    <Modal title="" onCancel={onClose} wide>
      {!a ? (
        <div className="p-8 text-center text-sm text-slate-400">Cargando…</div>
      ) : (
        <div className="space-y-4">
          <div className="flex items-center justify-between gap-2 flex-wrap">
            <div className="flex items-center gap-2">
              <ArticleStatusBadge status={a.status} />
              {a.seoScore != null && <span className="text-xs text-slate-500">SEO {a.seoScore}/100</span>}
              <span className="text-xs text-slate-400">{a.wordCount} palabras · {a.generatedBy}</span>
            </div>
            <div className="flex gap-1 flex-wrap">
              <button
                className="text-xs px-2 py-1 rounded border border-slate-300 hover:bg-slate-50"
                disabled={optimizeMut.isPending}
                onClick={() => optimizeMut.mutate()}>
                {optimizeMut.isPending ? 'Optimizando…' : 'Optimizar'}
              </button>
              {a.status !== 'Approved' && (
                <button className="text-xs px-2 py-1 rounded border border-sky-300 text-sky-700 hover:bg-sky-50" onClick={() => setStatus('Approved')}>
                  Aprobar
                </button>
              )}
              {a.status !== 'Published' && (
                <button className="text-xs px-2 py-1 rounded border border-emerald-300 text-emerald-700 hover:bg-emerald-50" onClick={() => setStatus('Published')}>
                  Marcar publicado
                </button>
              )}
            </div>
          </div>

          {a.optimizationNotes && (
            <div className="bg-amber-50 border border-amber-200 rounded p-3 text-xs text-amber-800 whitespace-pre-wrap">
              <b>Sugerencias de optimización:</b>{'\n'}{a.optimizationNotes}
            </div>
          )}

          <Field label="Título">
            <input className="input" value={title} onChange={(e) => { setTitle(e.target.value); setDirty(true); }} />
          </Field>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <Field label="Slug">
              <input className="input bg-slate-50" value={a.slug} readOnly />
            </Field>
            <Field label="Keyword objetivo">
              <input className="input bg-slate-50" value={a.targetKeyword} readOnly />
            </Field>
          </div>
          <Field label={`Meta description (${meta.length}/155)`}>
            <textarea className="input" rows={2} value={meta} onChange={(e) => { setMeta(e.target.value); setDirty(true); }} />
          </Field>

          <div>
            <div className="flex items-center justify-between mb-1">
              <label className="text-xs text-slate-500">Cuerpo (Markdown)</label>
              <button className="text-xs text-brand-600 hover:underline" onClick={() => copy(body, 'Markdown')}>Copiar</button>
            </div>
            <textarea
              className="input font-mono text-xs"
              rows={16}
              value={body}
              onChange={(e) => { setBody(e.target.value); setDirty(true); }}
            />
          </div>

          <details className="text-xs">
            <summary className="cursor-pointer text-slate-500">JSON-LD (schema.org) — pegar en el &lt;head&gt;</summary>
            <div className="flex justify-end mt-1">
              <button className="text-xs text-brand-600 hover:underline" onClick={() => copy(a.jsonLd, 'JSON-LD')}>Copiar</button>
            </div>
            <pre className="bg-slate-900 text-slate-100 rounded p-3 overflow-x-auto mt-1 text-[11px]">{a.jsonLd}</pre>
          </details>

          <div className="flex justify-between items-center pt-2 border-t border-slate-100">
            <button className="text-xs px-3 py-1 rounded border border-slate-300" onClick={onClose}>Cerrar</button>
            <button className="btn-primary" disabled={!dirty || patchMut.isPending} onClick={save}>
              {patchMut.isPending ? 'Guardando…' : 'Guardar cambios'}
            </button>
          </div>
        </div>
      )}
    </Modal>
  );
}

// ============================================================== SITE FORM

function SiteForm({
  site, submitting, onCancel, onSubmit
}: {
  site: SeoSite | null;
  submitting: boolean;
  onCancel: () => void;
  onSubmit: (body: Record<string, unknown>) => void;
}) {
  const [name, setName] = useState(site?.name ?? '');
  const [productKey, setProductKey] = useState(site?.productKey ?? '');
  const [domain, setDomain] = useState(site?.domain ?? '');
  const [blogBaseUrl, setBlogBaseUrl] = useState(site?.blogBaseUrl ?? '');
  const [language, setLanguage] = useState(site?.language ?? 'es');
  const [sector, setSector] = useState(site?.sector ?? '');
  const [audience, setAudience] = useState(site?.audience ?? '');
  const [productSummary, setProductSummary] = useState(site?.productSummary ?? '');
  const [brandVoice, setBrandVoice] = useState(site?.brandVoice ?? '');
  const [countries, setCountries] = useState((site?.targetCountries ?? []).join(', '));
  const [autoPublish, setAutoPublish] = useState(site?.autoPublish ?? false);
  const [weeklyTarget, setWeeklyTarget] = useState(site?.weeklyTarget ?? 3);

  return (
    <Modal title={site ? `Editar ${site.name}` : 'Nuevo sitio'} onCancel={onCancel} wide>
      <div className="space-y-3">
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label="Nombre"><input className="input" value={name} onChange={(e) => setName(e.target.value)} /></Field>
          <Field label="Product key (CRM, opcional)"><input className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)} placeholder="gymhero" /></Field>
          <Field label="Dominio"><input className="input" value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="gymhero.app" /></Field>
          <Field label="URL base del blog"><input className="input" value={blogBaseUrl} onChange={(e) => setBlogBaseUrl(e.target.value)} placeholder="https://gymhero.app/blog" /></Field>
          <Field label="Idioma"><input className="input" value={language} onChange={(e) => setLanguage(e.target.value)} /></Field>
          <Field label="Mercados (coma)"><input className="input" value={countries} onChange={(e) => setCountries(e.target.value)} placeholder="Argentina, Uruguay, Colombia" /></Field>
        </div>
        <Field label="Nicho / sector"><input className="input" value={sector} onChange={(e) => setSector(e.target.value)} placeholder="software de gestión para gimnasios" /></Field>
        <Field label="Audiencia"><textarea className="input" rows={2} value={audience} onChange={(e) => setAudience(e.target.value)} placeholder="dueños de gimnasios y entrenadores…" /></Field>
        <Field label="Resumen del producto"><textarea className="input" rows={2} value={productSummary} onChange={(e) => setProductSummary(e.target.value)} /></Field>
        <Field label="Voz de marca"><textarea className="input" rows={2} value={brandVoice} onChange={(e) => setBrandVoice(e.target.value)} placeholder="cercana, profesional, sin jerga…" /></Field>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 items-center">
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={autoPublish} onChange={(e) => setAutoPublish(e.target.checked)} />
            Agente 24/7 (genera borradores solo, siempre a revisión)
          </label>
          <Field label="Objetivo de artículos / semana">
            <input className="input" type="number" min={0} value={weeklyTarget} onChange={(e) => setWeeklyTarget(Number(e.target.value))} />
          </Field>
        </div>
      </div>
      <div className="flex justify-end gap-2 mt-4">
        <button className="text-xs px-3 py-1 rounded border border-slate-300" onClick={onCancel}>Cancelar</button>
        <button
          className="btn-primary"
          disabled={!name.trim() || submitting}
          onClick={() =>
            onSubmit({
              name: name.trim(),
              productKey: productKey.trim() || null,
              domain: domain.trim(),
              blogBaseUrl: blogBaseUrl.trim(),
              language: language.trim() || 'es',
              sector: sector.trim(),
              audience: audience.trim(),
              productSummary: productSummary.trim(),
              brandVoice: brandVoice.trim(),
              targetCountries: countries.split(',').map((c) => c.trim()).filter(Boolean),
              autoPublish,
              weeklyTarget
            })
          }>
          {submitting ? 'Guardando…' : site ? 'Guardar' : 'Crear'}
        </button>
      </div>
    </Modal>
  );
}

// ============================================================== SHARED

function Modal({ title, children, onCancel, wide }: { title: string; children: React.ReactNode; onCancel: () => void; wide?: boolean }) {
  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4 overflow-y-auto" onClick={onCancel}>
      <div
        className={`bg-white rounded-lg p-5 w-full my-8 ${wide ? 'max-w-3xl' : 'max-w-md'}`}
        onClick={(e) => e.stopPropagation()}>
        {title && <h3 className="font-semibold mb-3">{title}</h3>}
        {children}
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="text-xs text-slate-500">{label}</label>
      {children}
    </div>
  );
}
