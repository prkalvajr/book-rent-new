using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookRent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndicesDeConsultaDaAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_events_action",
                schema: "bookrent",
                table: "audit_events",
                columns: new[] { "action", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor",
                schema: "bookrent",
                table: "audit_events",
                columns: new[] { "actor", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_correlation_id",
                schema: "bookrent",
                table: "audit_events",
                column: "correlation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_events_action",
                schema: "bookrent",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_actor",
                schema: "bookrent",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_correlation_id",
                schema: "bookrent",
                table: "audit_events");
        }
    }
}
