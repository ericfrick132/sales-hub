# SalesHub

App para orquestar la venta de los 6 SaaS de Eric con 3 vendedores full-time, alimentada por Apify (Google Maps, Instagram, Meta Ads Library, Facebook Posts, TikTok) y con envío humanizado de WhatsApp vía Evolution API.

## Stack

- **Backend**: .NET 8 — `SalesHub.Api` (REST + JWT), `SalesHub.Workers` (BackgroundServices: sender humanizado, pipeline, monitor, competitors, trends), `SalesHub.Infrastructure` (EF Core + Postgres + Apify + Evolution), `SalesHub.Core` (dominio e interfaces).
- **Frontend**: React 18 + Vite + TypeScript + Tailwind + React Query + Zustand.
- **DB**: PostgreSQL 16.
- **Deploy**: DigitalOcean App Platform (spec en `.do/app.yaml`) o self-hosted via `docker compose`.
- **WhatsApp**: Evolution API self-hosted (no incluida acá; Eric ya tiene una corriendo en `64.227.3.140:8080`).

## Qué hace

1. Orquesta actors de Apify por producto, ciudad y categoría, respetando cooldowns por ciudad.
2. Deduplica leads por `(producto, whatsapp_phone)` y `(producto, place_id)`.
3. Asigna round-robin a los 3 vendedores según `verticals_whitelist`.
4. Envía mensajes WhatsApp humanizados — warmup, active hours, delays aleatorios, burst pauses, typing indicator, skip-day probability.
5. Da dashboards por vendedor y global, edita gauges humanización, CRUD de productos, y pantallas de competencia / tendencias.

## Arrancar en local

### Prerrequisitos
- .NET SDK 8.x
- Node 20+
- Docker + Docker Compose (para Postgres)

### 1) Variables de entorno

```bash
cp .env.example .env
# Edita .env con Apify token, Evolution API key, JWT key, admin email/password
```

### 2) Levantar Postgres

```bash
docker compose up -d postgres
```

### 3) Backend API

```bash
cd backend
dotnet restore
dotnet ef database update --project src/SalesHub.Infrastructure --startup-project src/SalesHub.Api
dotnet run --project src/SalesHub.Api
```

Swagger queda en `http://localhost:8080/swagger`.

### 4) Workers

En otra terminal:

```bash
cd backend
dotnet run --project src/SalesHub.Workers
```

Esto arranca los 5 BackgroundServices: `InstanceMonitorService`, `HumanizedSenderService`, `PipelineSchedulerService`, `CompetitorIngestWorker`, `TrendsIngestWorker`.

### 5) Frontend

```bash
cd frontend
npm install --legacy-peer-deps
npm run dev
```

Abrí `http://localhost:5173`. Login con el admin (`ADMIN_EMAIL` / `ADMIN_PASSWORD` del `.env`).

## Todo con docker-compose

```bash
docker compose up --build
```

Levanta Postgres + Api + Workers + Frontend. La app queda en `http://localhost`.

## Seeds iniciales

El primer arranque de la API (o Workers) seedea:

- **6 productos**: `gymhero`, `bookingpro_barber`, `bookingpro_salon`, `bookingpro_aesthetics` (inactivo), `unistock`, `playcrew`, `bunker`, `construction`.
- **~50 ciudades de Argentina** con población-bucket.
- **1 admin** (Eric) si `Seed:AdminEmail` + `Seed:AdminPassword` están seteados.
- **3 sellers sample** (`juan`, `pedro`, `maria`) con password `changeme`. Recomendado: entrar como admin a `/sellers`, resetear passwords y editar gauges.

## Flujo de un vendedor

1. Admin crea/edita vendedor en `/sellers`, le asigna verticals permitidas, setea password inicial.
2. Vendedor entra por `/login` con su email + password.
3. Va a `/connect` → genera QR de su Evolution instance → escanea con su celular.
4. Cuando el status queda `Connected`, activa el toggle "Comenzar envíos".
5. En `/leads` ve sus leads asignados. Cada uno tiene:
   - Link directo a WhatsApp con el mensaje pre-renderizado (click = abre WhatsApp).
   - Botón "Encolar envío" que deja que el sender humanizado lo mande automáticamente.
   - Cambio de status: `Assigned → Sent → Replied → Interested → DemoScheduled → Closed/Lost`.
6. `/dashboard` muestra sus métricas del día + leads activos.

## Flujo admin (Eric)

1. Login → `/admin` (dashboard global).
2. `/sellers` — CRUD + gauges. Gauges tiene 3 presets (Conservative/Balanced/Aggressive) o Custom con sliders.
3. `/products` — CRUD de las 6 apps. Template con placeholders + spin-text (`{Hola|Qué tal|Buenas}`).
4. `/pipeline` — 4 tabs (Google Maps / Instagram / Meta Ads Library / Facebook Posts / Google Places). Trigger manual + historia de corridas.
5. `/competitors` — agregar handles IG de competidores. Scrape on-demand. Comments negativos → prospectos calientes.
6. `/trends` — hashtags + top posts IG + TikTok por vertical.

## Humanización (defaults)

| Preset | Cap/día | Delay entre msgs | Burst | Pausa de burst | Skip-day |
|---|---|---|---|---|---|
| Conservative | 25 | 90–300s | 3 | 30–60 min | 10% |
| **Balanced (default)** | **50** | **45–180s** | **4** | **15–45 min** | **5%** |
| Aggressive | 100 | 25–90s | 6 | 10–30 min | 2% |

Además:
- **Warmup 7 días**: primer día ~1/3 del cap, linealmente sube a cap.
- **Active hours 10–21 (AR)**: fuera de esa ventana no envía.
- **Daily variance ±20%**: no siempre el número redondo.
- **Typing indicator** 3–8s antes de cada mensaje.
- **Mark incoming as read** antes de mandar outbound.

Todo configurable por vendedor en `/sellers`.

## Actors Apify usados

| Uso | Actor |
|---|---|
| Lead gen masivo | `compass/crawler-google-places` |
| Lead gen IG | `apify/instagram-scraper` |
| Lead gen (budget) | `curious_coder/facebook-ads-library-scraper` |
| Lead gen FB Pages | `apify/facebook-posts-scraper` |
| Enrich lead IG | `apify/instagram-profile-scraper` (on-demand) |
| Enrich website | `apify/website-content-crawler` (on-demand) |
| Google SERP admin | `apify/google-search-scraper` (on-demand) |
| Trends | `clockworks/tiktok-scraper` + `apify/instagram-scraper` |
| Competitors | `apify/instagram-scraper` (mode user) |

Todos los actor IDs se configuran en `appsettings:Apify:<Source>:ActorId`.

## Deploy a DigitalOcean App Platform

```bash
doctl apps create --spec .do/app.yaml
```

Subí el repo a GitHub y vinculá el App. Configurá las secrets en el panel DO:
- `Jwt__SigningKey`
- `Apify__Token`
- `Evolution__ApiKey`
- `Google__PlacesApiKey`
- `Google__OAuthClientId`
- `Seed__AdminEmail`
- `Seed__AdminPassword`

## Migraciones EF Core

Por **regla de proyecto (CLAUDE.md global):** nunca escribir migraciones a mano — siempre `dotnet ef migrations add <Name>` y `dotnet ef database update`.

```bash
cd backend
dotnet ef migrations add <NombreDeCambio> \
  --project src/SalesHub.Infrastructure --startup-project src/SalesHub.Api
dotnet ef database update \
  --project src/SalesHub.Infrastructure --startup-project src/SalesHub.Api
```

## Verificación end-to-end

Checklist post-deploy:

- [ ] Login admin funciona, `/admin` muestra métricas en 0.
- [ ] `/sellers` lista 3 sellers sample (juan/pedro/maria).
- [ ] Reset password de cada seller, compartir credenciales por canal seguro.
- [ ] Cada seller entra, va a `/connect`, genera QR, conecta WhatsApp.
- [ ] Admin dispara corrida manual en `/pipeline` → Google Maps → producto=gymhero, ciudad=Córdoba, max=30.
- [ ] Leads aparecen en `/leads` de cada seller (round-robin).
- [ ] Seller toggle "Comenzar envíos" → el sender empieza a procesar outbox.
- [ ] `/admin` muestra contador `todaySent` subiendo.
- [ ] `/competitors` + "Scrapear ahora" para un handle de IG conocido → aparecen posts + comentarios.

## Estructura del repo

```
sales-hub/
├── backend/
│   ├── SalesHub.sln
│   └── src/
│       ├── SalesHub.Core/          # Domain + abstractions
│       ├── SalesHub.Infrastructure/ # EF + Apify + Evolution + services
│       ├── SalesHub.Api/            # REST API
│       └── SalesHub.Workers/        # BackgroundServices
├── frontend/
│   ├── src/
│   │   ├── pages/                   # Rutas (login, leads, admin, etc.)
│   │   ├── components/              # Reutilizables (LeadTable, GaugeEditor, QrPanel, ...)
│   │   └── lib/                     # api, auth store, types
├── docker-compose.yml
├── .do/app.yaml                     # DigitalOcean spec
└── .env.example
```
