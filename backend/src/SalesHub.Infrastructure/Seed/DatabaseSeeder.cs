using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
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
    }

    private static async Task SeedSampleSellersAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.Sellers.AnyAsync(s => s.Role == SellerRole.Seller, ct)) return;
        var keys = new[] { ("juan", "Juan"), ("pedro", "Pedro"), ("maria", "Maria") };
        var allProducts = await db.Products.Select(p => p.ProductKey).ToListAsync(ct);
        var defaultPwd = "changeme"; // admin reassigns on /sellers
        foreach (var (key, name) in keys)
        {
            var seller = new Seller
            {
                Id = Guid.NewGuid(),
                SellerKey = key,
                DisplayName = name,
                Email = $"{key}@sales-hub.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPwd),
                Role = SellerRole.Seller,
                IsActive = true,
                VerticalsWhitelist = allProducts.ToList(),
                SendingEnabled = false,
                WarmupDays = 7
            };
            seller.EvolutionInstance = new EvolutionInstance
            {
                Id = Guid.NewGuid(),
                SellerId = seller.Id,
                InstanceName = $"seller_{key}"
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
                Categories = new() { "gimnasio", "crossfit", "funcional", "pilates", "yoga", "artes marciales", "dojo", "box" },
                MessageTemplate = "{Hola!|Qué tal!|Buenas!} Soy {seller}, fundador de GymHero.\n\n¿Cómo manejan las reservas de clases y pagos en {name}? Nuestra app envía recordatorios por WhatsApp y cobra las clases por Mercado Pago automáticamente.\n\nEstamos empezando operaciones en {city}. Precio final sin límite de alumnos: {price}. En 10 segundos creás tu cuenta:\n{checkout_url}\n\n7 días gratis sin tarjeta. Cualquier duda, escribime por acá!",
                CheckoutUrl = "https://gymhero.fitness", PriceDisplay = "$20.000/mes", DailyLimit = 60,
                TriggerHours = new() { 10, 14, 18 }, RequiresAssistedSale = false
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "bookingpro_barber", DisplayName = "TurnosPro — Barbería",
                Active = true, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "barbería", "peluquería masculina", "barber shop" },
                MessageTemplate = "{Hola!|Buenas!|Qué tal!} Soy {seller}. Vi {name} en {city}. ¿Cómo toman los turnos hoy? TurnosPro automatiza reservas + cobros + recordatorios por WhatsApp en un link.\n\n7 días gratis, sin tarjeta:\n{checkout_url}\n\nCualquier duda me escribís por acá!",
                CheckoutUrl = "https://turnos-pro.com/register?vertical=barbershop",
                PriceDisplay = "desde $15.000/mes", DailyLimit = 60,
                TriggerHours = new() { 11, 15, 19 }
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "bookingpro_salon", DisplayName = "TurnosPro — Salón",
                Active = true, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "peluquería", "salón de belleza", "estética", "spa", "manicuría" },
                MessageTemplate = "{Hola!|Buenas!} Soy {seller}. Vi {name} en {city}. ¿Siguen tomando turnos por WhatsApp o cuaderno?\n\nTurnosPro los toma online, manda recordatorios automáticos y cobra señas por Mercado Pago. 7 días gratis, sin tarjeta:\n{checkout_url}",
                CheckoutUrl = "https://turnos-pro.com/register?vertical=beautysalon",
                PriceDisplay = "desde $15.000/mes", DailyLimit = 60,
                TriggerHours = new() { 11, 15, 19 }
            },
            new()
            {
                Id = Guid.NewGuid(), ProductKey = "bookingpro_aesthetics", DisplayName = "TurnosPro — Estética",
                Active = false, Country = "AR", CountryName = "Argentina", RegionCode = "ar",
                Language = "es", PhonePrefix = "54",
                Categories = new() { "centro de estética", "dermatología estética", "spa facial" },
                MessageTemplate = "Hola! Soy {seller}. Vi {name} en {city}. ¿Cómo gestionan la agenda del centro? TurnosPro: turnos + cobros + recordatorios. 7 días gratis: {checkout_url}",
                CheckoutUrl = "https://turnos-pro.com/register?vertical=aesthetics",
                PriceDisplay = "desde $20.000/mes", DailyLimit = 40,
                TriggerHours = new() { 12, 16 }
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
                Categories = new() { "club de pádel", "canchas de pádel", "complejo deportivo", "pádel" },
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
