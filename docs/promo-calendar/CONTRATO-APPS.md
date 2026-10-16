# Promos automaticas — contrato sales-hub ↔ apps

sales-hub tiene un calendario de fechas (generales de AR + eventos por rubro). 7 dias antes
de cada fecha, por cada app, el motor:

1. genera el copy de la landing con Claude (usando la ficha REAL del producto),
2. elige la imagen del evento (foto publica CC0/dominio publico, guardada en sales-hub),
3. llama a la app (`POST /api/hub/promos`) → la app crea/actualiza el CUPON y la PROMO,
4. fuerza 1 posteo de promo por dia por red con el codigo y "link en bio".

En la bio de cada IG va UNA SOLA VEZ un link fijo:
`https://{dominio-marketing}/promo?utm_source=instagram&utm_medium=social&utm_content=bio`
La pagina `/promo` muestra la promo vigente (o redirige a la home si no hay).

## Auth

Header `X-Api-Key` = la MISMA key con la que la app ya valida `GET /api/hub/tenant-metrics`
(en sales-hub es `Hub:ApiKey`). Fail-closed: sin key configurada → 401.

## 1) `POST /api/hub/promos` (sales-hub → app)

```json
{
  "slug": "black-friday-2026",
  "code": "BLACK20",
  "title": "Black Friday",
  "headline": "Black Friday: 20% off los primeros 2 meses",
  "subheadline": "Texto de apoyo de 1-2 frases",
  "bullets": ["beneficio real 1", "beneficio real 2", "beneficio real 3"],
  "description": "Descripcion del evento (del calendario)",
  "discountPercent": 20,
  "durationMonths": 2,
  "startsAt": "2026-11-20T03:00:00Z",
  "endsAt": "2026-11-28T02:59:59Z",
  "imageUrl": "https://api.sales.efcloud.tech/api/posteos/assets/{id}.jpg",
  "imageCredit": "Foto: autor (CC0)"
}
```

Comportamiento (idempotente, se puede llamar N veces con el mismo slug):
- upsert de la Promo por `slug` (entidad nueva `Promo` en la app),
- upsert del Cupon por `code` (case-insensitive): `DiscountPercent`, `DurationMonths`,
  `ExpiresAt = endsAt`, `MaxUses = null`, `IsActive = true`, `Description = "Promo {title}"`.

Respuesta 200:
```json
{ "ok": true, "code": "BLACK20", "landingUrl": "https://gymhero.fitness/promo" }
```
`landingUrl` = dominio PUBLICO de marketing + `/promo` (sin query; sales-hub agrega los UTM).

## 2) `DELETE /api/hub/promos/{slug}` (sales-hub → app)

Desactiva la promo y su cupon. 200 `{ "ok": true }` (404 si no existe).

## 3) `GET /api/promos/active?slug=` (publico, AllowAnonymous)

- con `slug`: esa promo si esta vigente (`startsAt <= now <= endsAt` e `IsActive`),
- sin `slug`: la vigente con `startsAt` mas reciente.
- 404 si no hay.

```json
{
  "slug": "...", "code": "...", "title": "...", "headline": "...", "subheadline": "...",
  "bullets": ["..."], "description": "...", "discountPercent": 20, "durationMonths": 2,
  "endsAt": "...", "imageUrl": "...", "imageCredit": "...",
  "plans": [{ "code": "...", "name": "...", "price": 50000, "discountedPrice": 40000, "currency": "ARS" }]
}
```
`plans` = planes publicos REALES de la app (mismos que muestra la landing), con el descuento aplicado.

## 4) Pagina `/promo` (frontend de marketing, publica)

Estructura parecida a befitbycata.com/dia-de-la-madre:
1. Hero con la imagen (full-bleed con overlay oscuro para que se lea), `headline`, `subheadline`,
   badge "{X}% OFF los primeros {N} meses", CTA "Quiero la promo".
2. Caja del CODIGO grande con boton copiar + campo "Valida tu codigo" (prellenado con `?cupon=`
   o el codigo de la promo) que pega al endpoint de validacion de cupones de la app y muestra
   precio real tachado → precio con descuento.
3. `bullets` (beneficios reales).
4. Planes: precio REAL tachado vs precio con descuento, "por {N} meses, despues ${precio}".
   NADA de precios inflados: si la app tiene un "-33%" falso (promo.ts) NO se muestra en /promo.
5. Condiciones: "Valido hasta dd/mm", descuento los primeros N meses, cancelas cuando quieras.
6. CTA final repetido. `imageCredit` chiquito al pie.

- Si falta `utm_campaign` en la URL, agregarlo con `history.replaceState` (= slug).
- Todos los CTA llevan al alta/registro existente de la app arrastrando el codigo y los UTM.
- Sin promo vigente → redirect a la home conservando la query.

## 5) El cupon tiene que APLICARSE de verdad

El codigo que entra por `/promo` tiene que terminar descontando en el cobro real de la
suscripcion (`discountPercent` por `durationMonths` meses): guardarlo en el alta del tenant
(campo pendiente) y aplicarlo automaticamente cuando el tenant paga / se suscribe, por el
camino de pago que la app use de verdad (MercadoPago preapproval, etc.).
