using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BumpQueuedHotLeadPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-fix: las filas encoladas ANTES del fix de prioridad (a54b252) quedaron
            // grabadas con el default 50, así que los leads calientes ya en cola seguían
            // esperando detrás del outreach frío. Bump one-shot a 70 (mismo valor que asigna
            // OutboxEnqueueHelper hoy) para filas Scheduled (status=0) de leads con fuente
            // caliente (source >= 400: DemoSignup/ProductReengage/WhatsAppAd/Onboarding/MetaLeadAd).
            // No toca el 80 de onboarding ni filas ya enviadas. Idempotente.
            migrationBuilder.Sql(@"
                UPDATE message_outbox o
                SET priority = 70
                FROM leads l
                WHERE o.lead_id = l.id
                  AND o.status = 0
                  AND l.source >= 400
                  AND o.priority < 70;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-fix one-shot: no hay estado anterior que restaurar (todas esas filas
            // estaban en el default 50, y el enqueue actual ya escribe 70 solo).
        }
    }
}
