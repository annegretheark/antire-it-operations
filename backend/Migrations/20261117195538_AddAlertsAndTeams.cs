using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertsAndTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AlertId",
                table: "Tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Tickets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Eksisterende saker er meldt inn manuelt
                defaultValue: "manual");

            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MinSeverity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PriorityOverride = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    NotifyTeams = table.Column<bool>(type: "boolean", nullable: false),
                    CreateTickets = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PortalUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Occurrences = table.Column<int>(type: "integer", nullable: false),
                    RawPayload = table.Column<string>(type: "jsonb", nullable: true),
                    TicketId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamsNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamsNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamsSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    EncryptedWebhookUrl = table.Column<string>(type: "text", nullable: true),
                    NotifyCriticalManualTickets = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamsSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AlertId",
                table: "Tickets",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_TenantId_Source",
                table: "AlertRules",
                columns: new[] { "TenantId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_TenantId_Source_ExternalId",
                table: "Alerts",
                columns: new[] { "TenantId", "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_TicketId",
                table: "Alerts",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamsNotifications_TenantId_Status",
                table: "TeamsNotifications",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamsSettings_TenantId",
                table: "TeamsSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Alerts_AlertId",
                table: "Tickets",
                column: "AlertId",
                principalTable: "Alerts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Alerts_AlertId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "TeamsNotifications");

            migrationBuilder.DropTable(
                name: "TeamsSettings");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_AlertId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AlertId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Tickets");
        }
    }
}
