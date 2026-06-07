using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Seed;

public static class DatabaseSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db, string? adminEmail, string? adminPassword, CancellationToken ct = default)
    {
        await SeedProductsAsync(db, ct);
        await SeedCitiesAsync(db, ct);
        await SeedAdminAsync(db, adminEmail, adminPassword, ct);
        await SeedSampleSellersAsync(db, ct);
        await BackfillCategoryOverridesAsync(db, ct);
        await SeedPostingProfilesAsync(db, ct);
    }

    /// <summary>
    /// Para productos que ya tienen categorías y MessageSteps default
    /// configurado pero todavía no tienen CategoryCadences, crea un
    /// override por cada categoría con copia de los MessageSteps. Esto da
    /// al admin un punto de partida — abrir un producto y ver una tab
    /// por cada vertical, ya pre-poblada con la cadencia default —
    /// sin tener que clickear "+ Override" 9 veces.
    ///
    /// Skip si:
    /// - El producto ya tiene CategoryCadences (ya configurado).
    /// - No tiene Categories (no hay verticales que personalizar).
    /// - MessageSteps vacío (no hay nada para clonar; los overrides
    ///   vacíos se filtrarían en el próximo save desde el frontend).
    /// </summary>
    private static async Task BackfillCategoryOverridesAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var products = await db.Products.ToListAsync(ct);
        var changed = false;
        foreach (var p in products)
        {
            if (p.CategoryCadences is { Count: > 0 }) continue;
            if (p.Categories is null or { Count: 0 }) continue;
            var defaultSteps = p.MessageSteps ?? new();
            if (defaultSteps.Count == 0) continue;

            p.CategoryCadences = p.Categories
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => new CategoryCadence
                {
                    Category = c.Trim(),
                    Steps = CloneSteps(defaultSteps)
                })
                .ToList();
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static List<MessageStep> CloneSteps(List<MessageStep> src) =>
        src.Select(s => new MessageStep
        {
            Text = s.Text,
            DelaySeconds = s.DelaySeconds,
            MediaAssetId = s.MediaAssetId,
            MediaAssetIds = new List<Guid>(s.MediaAssetIds ?? new())
        }).ToList();

    /// <summary>
    /// Pre-popula un PostingProfile por producto con la identidad de marca real
    /// extraída de cada repo/landing (colores, fuentes, logo, audiencia, tono,
    /// pilares). Arrancan con Enabled=false y sin canales de Buffer mapeados —
    /// el admin completa BufferChannelsJson y prende cuando conecta las redes.
    /// Idempotente: si ya hay perfiles, no hace nada.
    /// </summary>
    private static async Task SeedPostingProfilesAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.PostingProfiles.AnyAsync(ct)) return;

        var profiles = new List<PostingProfile>
        {
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "gymhero", Enabled = false,
                BrandColorsJson = "{\"primary\":\"#DDFD42\",\"background\":\"#0A0A0A\",\"accent\":\"#DDFD42\",\"text\":\"#F5F5F5\"}",
                BrandFonts = "Bricolage Grotesque / DM Sans (mono: JetBrains Mono)",
                BrandLogoUrl = "GymHero/src/frontend/marketing/public/images/logo.png",
                BrandVoice = "Directo, sin vueltas, rioplatense. Habla del dolor del dueño de gym (Excel, perseguir morosos) y promete automatización y ahorro de tiempo. Estética 'bunker' negro + lima.",
                TargetAudience = "Dueños de gimnasios, boxes de CrossFit y estudios de fitness (B2B)",
                ContentPillars = new() { "Cobros automáticos sin perseguir morosos", "Semáforo de acceso en recepción (QR)", "Clases llenas, menos no-shows", "Dashboard único, adiós Excel", "Rutinas personalizadas", "Setup en 1 día incluido", "Sin permanencia, 7 días gratis", "Integrado con MercadoPago + WhatsApp" },
                BrandGuidelines = "Idioma es-AR (voseo). Ejemplos de copy: 'Dejá de perseguir morosos. GymHero cobra por vos.' / 'Todo lo que tu gym necesita. Nada que no.' / 'Tu gym merece dejar de perder plata.' Mostrar métricas reales, no fluff.",
                PostHours = new() { 10, 18 }, PostsPerDay = 1,
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "turnospro", Enabled = false,
                BrandColorsJson = "{\"primary\":\"#1E5E3F\",\"background\":\"#F4EFE6\",\"accent\":\"#E8593C\",\"text\":\"#171410\"}",
                BrandFonts = "Fraunces / Space Grotesk (mono: JetBrains Mono)",
                BrandLogoUrl = "TurnosPro/src/marketing-next/public/logo.png",
                BrandVoice = "Profesional pero cálido, editorial. Voseo rioplatense. Enfocado en resultados (menos cancelaciones) y simplicidad. Paleta papel cálido + tinta.",
                TargetAudience = "Peluquerías, barberías, estética, consultorios y profesionales con sistema de turnos (B2B)",
                ContentPillars = new() { "Reservas online 24/7", "Pagos anticipados con seña (MercadoPago)", "Recordatorios automáticos por WhatsApp", "Agenda inteligente multi-profesional", "Catálogo digital de servicios", "Gestión integral de clientes", "Setup rápido sin apps", "Soporte humano y mejora continua" },
                BrandGuidelines = "Idioma es-AR (voseo). Ejemplos: 'Agendá más con tu sitio listo para reservas.' / '80% menos cancelaciones: señas + recordatorios por WhatsApp.' Resaltar número duro + solución.",
                PostHours = new() { 10, 18 }, PostsPerDay = 1,
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "playcrew", Enabled = false,
                BrandColorsJson = "{\"primary\":\"#C8FC2C\",\"background\":\"#111827\",\"accent\":\"#C8FC2C\",\"text\":\"#FFFFFF\"}",
                BrandFonts = "Lexend / Inter",
                BrandLogoUrl = "PlayCrew/src/frontend/public/png-sin-fondo-icono.png",
                BrandVoice = "Directo, moderno, orientado a la acción. Habla de sacar fricción y llenar canchas. Dark theme + lima.",
                TargetAudience = "Dueños de clubes y canchas de pádel y tenis (B2B); también jugadores (B2C)",
                ContentPillars = new() { "Reservas automáticas 24/7", "Pagos y señas online", "Analytics de ocupación y revenue", "Membresías con auto-cobro", "Menos no-shows (recordatorios)", "Link público branded del club", "Gestión multi-cancha", "Ranking/ELO para torneos" },
                BrandGuidelines = "Idioma es-AR. Ejemplos: 'Canchas llenas, sin tomar reservas por WhatsApp.' / 'Reservá tu próximo partido en 3 simples pasos.' Tono game-changer.",
                PostHours = new() { 10, 18 }, PostsPerDay = 1,
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "construction", Enabled = false,
                BrandColorsJson = "{\"primary\":\"#1F3A60\",\"background\":\"#F4EEE0\",\"accent\":\"#D69531\",\"text\":\"#0E0F11\"}",
                BrandFonts = "DM Serif Display / Inter (mono: IBM Plex Mono)",
                BrandLogoUrl = "", // identidad tipográfica (sin asset de logo)
                BrandVoice = "Técnico pero accesible, como un set de planos: preciso, ordenado, con anotaciones. Español directo, transparencia y control del caos de obra.",
                TargetAudience = "Arquitectos, estudios de arquitectura y constructoras (B2B)",
                ContentPillars = new() { "Gastos en tiempo real (ARS/USD)", "Documentos y planos versionados", "Control de contratistas y proveedores", "Timeline visual y avance de obra", "Dashboard multi-obra", "Transparencia por rol", "Biblioteca de materiales", "Presupuesto preciso ($/m²)" },
                BrandGuidelines = "Idioma es-AR. Ejemplos: 'Tu obra no vive en siete Excels. Vive acá.' / 'Planos versionados. Sin ¿cuál es el bueno?.' Estética de planos/blueprint.",
                PostHours = new() { 10, 18 }, PostsPerDay = 1,
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "unistock", Enabled = false,
                BrandColorsJson = "{\"primary\":\"#384DF4\",\"background\":\"#FAF9F6\",\"accent\":\"#F6D058\",\"text\":\"#0A0A0A\"}",
                BrandFonts = "Geist / Geist (mono: JetBrains Mono)",
                BrandLogoUrl = "CLIENTS/InventSync - AR/src/frontend/public/logo192.png",
                BrandVoice = "Directo, honesto de depósito, sin fluff. Reconoce el dolor (revender stock fantasma) antes de resolverlo. Editorial y data-driven.",
                TargetAudience = "PyMEs que fabrican/distribuyen y venden multicanal — MercadoLibre, Tienda Nube, WhatsApp (B2B)",
                ContentPillars = new() { "Sync de stock multicanal en tiempo real", "Fabricación con BOM/recetas", "Reposición sugerida por demanda", "CRM unificado ventas + stock", "Analytics por canal (margen/rotación)", "Multi-depósito y lotes", "Trial 14 días, setup en el día", "Soporte en español LATAM" },
                BrandGuidelines = "Idioma es-AR. Ejemplos: 'Vendés en tres canales. Te queda una unidad. ¿A quién le fallás?' / 'Un stock, todos los canales.' Reconocer dolor, después resolver.",
                PostHours = new() { 10, 18 }, PostsPerDay = 1,
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "bunker", Enabled = false,
                BrandColorsJson = "{\"primary\":\"#DDFD42\",\"background\":\"#0A0A0A\",\"accent\":\"#10B981\",\"text\":\"#FFFFFF\"}",
                BrandFonts = "Inter / Inter (serif accent: Instrument Serif)",
                BrandLogoUrl = "CLIENTS/BUNKER - AR/src/frontend/public/logo192.png",
                BrandVoice = "Profesional pero cercano. Data-driven, tecnología y confianza en la relación coach-atleta. 'Entrená con propósito'. Negro premium + lima.",
                TargetAudience = "Personal trainers, entrenadores y coaches deportivos (B2B)",
                ContentPillars = new() { "Planificación inteligente de entrenamientos", "Tracking de progreso (RPE/RIR)", "Comunicación coach–atleta", "Gestión de equipos y grupos", "Multi-dispositivo (app + web)", "Gratis para empezar", "Cobros con MercadoPago", "Estética deportiva premium" },
                BrandGuidelines = "Idioma es-AR. Ejemplos: 'Entrená con propósito, mejorá con datos.' / 'Tu gimnasio, digitalizado.' Tono profesional, deportivo.",
                PostHours = new() { 10, 18 }, PostsPerDay = 1,
            },
        };

        db.PostingProfiles.AddRange(profiles);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedSampleSellersAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.Sellers.AnyAsync(s => s.Role == SellerRole.Seller, ct)) return;
        var verticals = new List<string> { "gymhero", "playcrew" };
        var defaultPwd = "changeme"; // admin reassigns on /sellers
        var sellers = new[]
        {
            new { Key = "martu",  Name = "Martu",  Email = "Burgosmarti723@gmail.com",
                Regions = new List<string> { "Rosario", "Santa Fe" } },
            new { Key = "brian",  Name = "Brian",  Email = "Briandmsc@gmail.com",
                // GBA Oeste primer cordón — partidos individuales para que matcheen city de Google Maps.
                Regions = new List<string> { "Morón", "Tres de Febrero", "Hurlingham", "Ituzaingó", "La Matanza" } },
            new { Key = "thiago", Name = "Thiago", Email = "scrivanothiago@gmail.com",
                // CABA con variantes que devuelve Google Maps (a veces "Capital Federal", a veces el nombre completo).
                Regions = new List<string> { "CABA", "Capital Federal", "Ciudad Autónoma de Buenos Aires" } },
            new { Key = "zeke",   Name = "Zeke",   Email = "eznex7@gmail.com",
                // GBA Norte primer cordón
                Regions = new List<string> { "Vicente López", "San Isidro", "San Fernando", "Tigre" } }
        };
        foreach (var s in sellers)
        {
            var seller = new Seller
            {
                Id = Guid.NewGuid(),
                SellerKey = s.Key,
                DisplayName = s.Name,
                Email = s.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPwd),
                Role = SellerRole.Seller,
                IsActive = true,
                VerticalsWhitelist = verticals.ToList(),
                RegionsAssigned = s.Regions,
                SendingEnabled = false,
                WarmupDays = 7
            };
            seller.EvolutionInstance = new EvolutionInstance
            {
                Id = Guid.NewGuid(),
                SellerId = seller.Id,
                InstanceName = $"seller_{s.Key}"
            };
            db.Sellers.Add(seller);
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedProductsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct)) return;

        var products = new List<Product>
        {
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "gymhero", DisplayName = "GymHero",
                Active = true, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "gimnasio", "crossfit", "yoga", "pilates", "taekwondo", "funcional", "running", "natación", "danza" },
                MessageTemplate = "{Hola!|Qué tal!|Buenas!} Soy {seller}, fundador de GymHero.\n\n¿Cómo manejan las reservas de clases y pagos en {name}? Nuestra app envía recordatorios por WhatsApp y cobra las clases por Mercado Pago automáticamente.\n\nEstamos empezando operaciones en {city}. Precio final sin límite de alumnos: {price}. En 10 segundos creás tu cuenta:\n{checkout_url}\n\n7 días gratis sin tarjeta. Cualquier duda, escribime por acá!",
                CheckoutUrl = "https://gymhero.fitness", PriceDisplay = "$20.000/mes", DailyLimit = 60,
                TriggerHours = new() { 10, 14, 18 }, RequiresAssistedSale = false
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "unistock", DisplayName = "UniStock",
                Active = true, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "distribuidora", "mayorista", "importador", "tienda de ropa", "e-commerce" },
                MessageTemplate = "Hola! Soy {seller}. ¿Vendés en MercadoLibre y TiendaNube? UniStock sincroniza stock entre canales y evita sobreventas. Un cliente recuperó 15h/semana.\n\nTe muestro una demo rápida de 15 min? {checkout_url}",
                CheckoutUrl = "https://unistock-zexev.ondigitalocean.app/", PriceDisplay = "desde USD 49/mes",
                DailyLimit = 40, TriggerHours = new() { 11, 15, 19 }, RequiresAssistedSale = true
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "playcrew", DisplayName = "PlayCrew",
                Active = true, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "pádel", "tenis", "club de pádel", "canchas de pádel", "club de tenis" },
                MessageTemplate = "Hola! Soy {seller}. Vi {name} en {city}. ¿Cómo toman las reservas del club? PlayCrew está hecho para clubes de Argentina (Playtomic casi no opera acá). Te muestro cómo anda? {checkout_url}",
                CheckoutUrl = "https://playcrewpadel.com/", PriceDisplay = "a confirmar",
                DailyLimit = 40, TriggerHours = new() { 10, 14, 18 }, RequiresAssistedSale = true
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "bunker", DisplayName = "Bunker (ConquerApp)",
                Active = true, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "personal trainer", "entrenador", "coach fitness", "nutricionista deportivo" },
                MessageTemplate = "Hola! Soy {seller}. Vi que entrenás en {city}. Bunker es una app para que los coaches armen rutinas, manejen clientes y cobren online. 7 días gratis: {checkout_url}",
                CheckoutUrl = "https://bunker-app.com", PriceDisplay = "desde $12.000/mes",
                DailyLimit = 40, TriggerHours = new() { 12, 16, 19 }
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "construction", DisplayName = "ObraCloud",
                Active = true, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "constructora", "empresa de construcción", "estudio de arquitectura" },
                MessageTemplate = "Hola! Soy {seller}. ObraCloud gestiona proyectos de obra en un panel: tareas, costos, fotos, subcontratistas. ¿Te paso una demo de 15 min? {checkout_url}",
                CheckoutUrl = "https://construction-manager-w9azx.ondigitalocean.app/", PriceDisplay = "desde USD 99/mes",
                DailyLimit = 30, TriggerHours = new() { 11, 15 }, RequiresAssistedSale = true
            }
        };

        db.Products.AddRange(products);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedCitiesAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.Cities.AnyAsync(ct)) return;
        var rows = CitySeedData.Argentina;
        db.Cities.AddRange(rows.Select(r => new CityQueue
        {
            Id = Guid.NewGuid(),
            Country = r.Country,
            Province = r.Province,
            City = r.City,
            PopulationBucket = r.Bucket
        }));
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedAdminAsync(ApplicationDbContext db, string? email, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;
        if (await db.Sellers.AnyAsync(s => s.Email == email, ct)) return;

        db.Sellers.Add(new Seller
        {
            Id = Guid.NewGuid(),
            SellerKey = "admin",
            DisplayName = "Eric",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = SellerRole.Admin,
            IsActive = true,
            SendingEnabled = false,
            WarmupDays = 0
        });
        await db.SaveChangesAsync(ct);
    }
}

public static class CitySeedData
{
    public record Row(string Country, string Province, string City, PopulationBucket Bucket);

    public static readonly Row[] Argentina =
    {
        new("AR", "Buenos Aires", "CABA", PopulationBucket.Mega),
        new("AR", "Buenos Aires", "La Matanza", PopulationBucket.Big),
        new("AR", "Buenos Aires", "Morón", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Tres de Febrero", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Hurlingham", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Ituzaingó", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Vicente López", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "San Isidro", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "San Fernando", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Tigre", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "La Plata", PopulationBucket.Big),
        new("AR", "Buenos Aires", "Mar del Plata", PopulationBucket.Big),
        new("AR", "Buenos Aires", "Bahía Blanca", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Tandil", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Pergamino", PopulationBucket.Medium),
        new("AR", "Buenos Aires", "Olavarría", PopulationBucket.Small),
        new("AR", "Buenos Aires", "Junín", PopulationBucket.Small),
        new("AR", "Buenos Aires", "Pehuajó", PopulationBucket.Town),
        new("AR", "Buenos Aires", "9 de Julio", PopulationBucket.Town),
        new("AR", "Córdoba", "Córdoba", PopulationBucket.Big),
        new("AR", "Córdoba", "Villa María", PopulationBucket.Medium),
        new("AR", "Córdoba", "Río Cuarto", PopulationBucket.Medium),
        new("AR", "Córdoba", "San Francisco", PopulationBucket.Small),
        new("AR", "Córdoba", "Villa Carlos Paz", PopulationBucket.Small),
        new("AR", "Córdoba", "Alta Gracia", PopulationBucket.Small),
        new("AR", "Santa Fe", "Rosario", PopulationBucket.Big),
        new("AR", "Santa Fe", "Santa Fe", PopulationBucket.Big),
        new("AR", "Santa Fe", "Rafaela", PopulationBucket.Medium),
        new("AR", "Santa Fe", "Venado Tuerto", PopulationBucket.Small),
        new("AR", "Santa Fe", "Reconquista", PopulationBucket.Small),
        new("AR", "Mendoza", "Mendoza", PopulationBucket.Big),
        new("AR", "Mendoza", "San Rafael", PopulationBucket.Medium),
        new("AR", "Mendoza", "Godoy Cruz", PopulationBucket.Medium),
        new("AR", "Mendoza", "Maipú", PopulationBucket.Small),
        new("AR", "Mendoza", "Luján de Cuyo", PopulationBucket.Small),
        new("AR", "Tucumán", "San Miguel de Tucumán", PopulationBucket.Big),
        new("AR", "Tucumán", "Yerba Buena", PopulationBucket.Small),
        new("AR", "Salta", "Salta", PopulationBucket.Medium),
        new("AR", "Jujuy", "San Salvador de Jujuy", PopulationBucket.Medium),
        new("AR", "Entre Ríos", "Paraná", PopulationBucket.Medium),
        new("AR", "Entre Ríos", "Concordia", PopulationBucket.Medium),
        new("AR", "Entre Ríos", "Gualeguaychú", PopulationBucket.Small),
        new("AR", "Corrientes", "Corrientes", PopulationBucket.Medium),
        new("AR", "Misiones", "Posadas", PopulationBucket.Medium),
        new("AR", "Misiones", "Oberá", PopulationBucket.Small),
        new("AR", "Chaco", "Resistencia", PopulationBucket.Medium),
        new("AR", "Santiago del Estero", "Santiago del Estero", PopulationBucket.Medium),
        new("AR", "La Rioja", "La Rioja", PopulationBucket.Medium),
        new("AR", "San Juan", "San Juan", PopulationBucket.Medium),
        new("AR", "Neuquén", "Neuquén", PopulationBucket.Medium),
        new("AR", "Río Negro", "San Carlos de Bariloche", PopulationBucket.Medium),
        new("AR", "Río Negro", "General Roca", PopulationBucket.Small),
        new("AR", "Río Negro", "Cipolletti", PopulationBucket.Small),
        new("AR", "Chubut", "Comodoro Rivadavia", PopulationBucket.Medium),
        new("AR", "Chubut", "Puerto Madryn", PopulationBucket.Small),
        new("AR", "Chubut", "Trelew", PopulationBucket.Small),
        new("AR", "Santa Cruz", "Río Gallegos", PopulationBucket.Small),
        new("AR", "Tierra del Fuego", "Ushuaia", PopulationBucket.Small),
        new("AR", "La Pampa", "Santa Rosa", PopulationBucket.Small),
        new("AR", "San Luis", "San Luis", PopulationBucket.Small),
        new("AR", "Catamarca", "San Fernando del Valle de Catamarca", PopulationBucket.Medium),
        new("AR", "Formosa", "Formosa", PopulationBucket.Medium)
    };
}
