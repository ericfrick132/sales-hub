# SeoSeed — artículos del blog escritos como archivos

Los artículos que viven en esta carpeta se importan al blog central (tablas
`SeoSites` / `SeoArticles`) **cada vez que arranca la API**, sin pasar por el
`SeoController` autenticado. Quedan publicados de inmediato y se sirven en
`https://api.sales.efcloud.tech/blog-feed/{siteKey}/{slug}`, que cada app
expone en su propio dominio bajo `https://{dominio}/blog/{slug}/`.

El import lo hace `SalesHub.Infrastructure/Seed/SeoSeedImporter.cs`, invocado
desde `Program.cs` justo después de las migraciones. Corre solo en el rol API
(el componente `workers` de DigitalOcean usa `SalesHub.Workers/Program.cs`, que
no lo llama). Se puede apagar con `SALESHUB_SEO_SEED_IMPORT=false`.

## Estructura

```
SeoSeed/
  <siteKey>/
    <slug>.json   # metadata
    <slug>.md     # cuerpo en Markdown
```

- `<siteKey>` = `ProductKey` de un `SeoSite` activo (ej. `gymhero`, `turnospro`,
  `playcrew`, `archicloud`). Se compara sin distinguir mayúsculas.
- `<slug>` = slug final de la URL. Solo minúsculas, números y guiones.
- Los dos archivos deben tener el mismo nombre base; sin el `.md` el artículo se omite.

## `<slug>.json`

```json
{
  "siteKey": "gymhero",
  "slug": "software-gestion-gimnasios-vs-excel",
  "title": "Software de gestión para gimnasios vs Excel",
  "metaDescription": "Hasta 160 caracteres, con la keyword.",
  "targetKeyword": "software gestión gimnasios",
  "contentType": "Comparison",
  "publishedAt": "2026-09-03",
  "faq": [
    { "question": "¿...?", "answer": "..." }
  ],
  "jsonLd": "{\"@context\":\"https://schema.org\",\"@type\":\"Article\",...}"
}
```

| Campo             | Obligatorio | Notas |
|-------------------|-------------|-------|
| `siteKey`         | no          | si falta se usa el nombre de la carpeta |
| `slug`            | no          | si falta se usa el nombre del archivo |
| `title`           | sí*         | *si falta se toma del primer `# Título` del `.md` |
| `metaDescription` | recomendado | máx. 1024 caracteres (se trunca) |
| `targetKeyword`   | recomendado | máx. 256 caracteres |
| `contentType`     | no          | `Article` (default), `Guide`, `Faq`, `Comparison`, `Landing` |
| `publishedAt`     | no          | fecha ISO; se interpreta en UTC. Default: momento del import |
| `faq`             | no          | lista de `{ "question", "answer" }`; alimenta el FAQPage visible + schema |
| `jsonLd`          | no          | string con el JSON-LD (también se acepta un objeto JSON) o `""` |

## `<slug>.md`

Cuerpo completo en Markdown (Markdig, extensiones avanzadas). **La primera línea
debe ser `# <title>`**: la plantilla del blog no agrega el `<h1>` a partir del
título, así que si el cuerpo no empieza con `# ` el importador lo antepone.

## Reglas del import

- Idempotente: se busca el artículo por (`SiteId`, `Slug`).
- Si no existe → se crea con `Status = Published`, `PublishedAt = publishedAt`,
  `GeneratedBy = "seed-import"`, `PublishedUrl = https://{dominio}/blog/{slug}/`.
- Si existe con `GeneratedBy = "seed-import"` → se sobreescriben título, meta,
  keyword, cuerpo, FAQ, JSON-LD, tipo y `WordCount`; se conservan `Id`,
  `CreatedAt`, `PublishedAt` y `Status` (si alguien lo archivó a mano, queda
  archivado). `UpdatedAt` solo cambia cuando cambió algo.
- Si existe con otro `GeneratedBy` (lo generó el motor o se creó a mano) → **no se
  toca**; se loguea y se sigue.
- Un archivo roto no frena el resto ni el arranque de la API: se loguea el error
  y al final una línea resumen (creados / actualizados / sin cambios / omitidos / con error).
- Borrar los archivos del repo **no** borra el artículo de la base; archivalo
  desde el panel SEO si querés sacarlo del blog.

## Cómo se despliega

`SalesHub.Api.csproj` incluye `SeoSeed\**\*` como contenido que se copia al
publish, así que la carpeta viaja dentro de la imagen Docker junto al DLL
(`/app/SeoSeed`). El importador la resuelve con `AppContext.BaseDirectory`
y, si no está, con el content root del proyecto (para `dotnet run`).
