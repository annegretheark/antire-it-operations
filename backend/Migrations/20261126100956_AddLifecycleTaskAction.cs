using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddLifecycleTaskAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "LifecycleTemplateTasks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "LifecycleTasks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Standardoppgaven «Deaktiver brukerkonto i Entra ID» får knappen, både i
            // malene og i offboarding-prosesser som ikke er avsluttet
            migrationBuilder.Sql("""
                UPDATE "LifecycleTemplateTasks"
                SET "Action" = 'disable_entra_account'
                WHERE "Type" = 'offboarding' AND "Title" = 'Deaktiver brukerkonto i Entra ID' AND "Action" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "LifecycleTasks" AS t
                SET "Action" = 'disable_entra_account'
                FROM "LifecycleProcesses" AS p
                WHERE t."ProcessId" = p."Id"
                  AND p."Type" = 'offboarding' AND p."Status" = 'active'
                  AND t."Title" = 'Deaktiver brukerkonto i Entra ID' AND t."Action" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Action",
                table: "LifecycleTemplateTasks");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "LifecycleTasks");
        }
    }
}
