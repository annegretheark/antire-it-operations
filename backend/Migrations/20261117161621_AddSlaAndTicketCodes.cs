using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaAndTicketCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstRespondedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolutionBreachedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolutionDueAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResponseBreachedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResponseDueAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SlaTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResponseMinutes = table.Column<int>(type: "integer", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    TwentyFourSeven = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaTargets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SlaTargets_TenantId_Priority",
                table: "SlaTargets",
                columns: new[] { "TenantId", "Priority" },
                unique: true);

            // 0.6: status og prioritet går fra norske strenger til engelske koder.
            // Revisjonsloggen beholder de gamle verdiene; frontend viser begge.
            // Saker uten prioritet (fra før prioritet fantes) får normal.
            migrationBuilder.Sql("""
                UPDATE "Tickets" SET "Status" = CASE "Status"
                    WHEN 'Ny' THEN 'new'
                    WHEN 'Under behandling' THEN 'in_progress'
                    WHEN 'Løst' THEN 'resolved'
                    ELSE "Status" END;

                UPDATE "Tickets" SET "Priority" = 'Normal' WHERE "Priority" IS NULL OR "Priority" = '';

                UPDATE "Tickets" SET "Priority" = CASE "Priority"
                    WHEN 'Lav' THEN 'low'
                    WHEN 'Normal' THEN 'normal'
                    WHEN 'Høy' THEN 'high'
                    WHEN 'Kritisk' THEN 'critical'
                    ELSE "Priority" END;

                UPDATE "ServiceCatalogItems" SET "DefaultPriority" = CASE "DefaultPriority"
                    WHEN 'Lav' THEN 'low'
                    WHEN 'Normal' THEN 'normal'
                    WHEN 'Høy' THEN 'high'
                    WHEN 'Kritisk' THEN 'critical'
                    ELSE "DefaultPriority" END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Tickets" SET "Status" = CASE "Status"
                    WHEN 'new' THEN 'Ny'
                    WHEN 'in_progress' THEN 'Under behandling'
                    WHEN 'resolved' THEN 'Løst'
                    ELSE "Status" END;

                UPDATE "Tickets" SET "Priority" = CASE "Priority"
                    WHEN 'low' THEN 'Lav'
                    WHEN 'normal' THEN 'Normal'
                    WHEN 'high' THEN 'Høy'
                    WHEN 'critical' THEN 'Kritisk'
                    ELSE "Priority" END;

                UPDATE "ServiceCatalogItems" SET "DefaultPriority" = CASE "DefaultPriority"
                    WHEN 'low' THEN 'Lav'
                    WHEN 'normal' THEN 'Normal'
                    WHEN 'high' THEN 'Høy'
                    WHEN 'critical' THEN 'Kritisk'
                    ELSE "DefaultPriority" END;
                """);

            migrationBuilder.DropTable(
                name: "SlaTargets");

            migrationBuilder.DropColumn(
                name: "FirstRespondedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolutionBreachedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolutionDueAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResponseBreachedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResponseDueAt",
                table: "Tickets");
        }
    }
}
