using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TicketCategoryGroups_Category_EntraGroupId",
                table: "TicketCategoryGroups");

            migrationBuilder.DropIndex(
                name: "IX_Employees_EmployeeNumber",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_Employees_EntraObjectId",
                table: "Employees");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Tickets",
                type: "character varying(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "TicketCategoryGroups",
                type: "character varying(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "Employees",
                type: "character varying(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "EmployeeResponsibilities",
                type: "character varying(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "AccessRequests",
                type: "character varying(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            // Eksisterende data ble laget i tenanten appen har brukt så langt
            // (AzureAd:TenantId i appsettings.json). Deretter fjernes standardverdien,
            // slik at nye rader alltid må få tenant fra innloggingen.
            migrationBuilder.Sql("""
                UPDATE "Tickets"                  SET "TenantId" = '3af5c92b-38f9-48ce-8746-82bb4c113032';
                UPDATE "TicketCategoryGroups"     SET "TenantId" = '3af5c92b-38f9-48ce-8746-82bb4c113032';
                UPDATE "Employees"                SET "TenantId" = '3af5c92b-38f9-48ce-8746-82bb4c113032';
                UPDATE "EmployeeResponsibilities" SET "TenantId" = '3af5c92b-38f9-48ce-8746-82bb4c113032';
                UPDATE "AccessRequests"           SET "TenantId" = '3af5c92b-38f9-48ce-8746-82bb4c113032';

                ALTER TABLE "Tickets"                  ALTER COLUMN "TenantId" DROP DEFAULT;
                ALTER TABLE "TicketCategoryGroups"     ALTER COLUMN "TenantId" DROP DEFAULT;
                ALTER TABLE "Employees"                ALTER COLUMN "TenantId" DROP DEFAULT;
                ALTER TABLE "EmployeeResponsibilities" ALTER COLUMN "TenantId" DROP DEFAULT;
                ALTER TABLE "AccessRequests"           ALTER COLUMN "TenantId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId",
                table: "Tickets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategoryGroups_TenantId_Category_EntraGroupId",
                table: "TicketCategoryGroups",
                columns: new[] { "TenantId", "Category", "EntraGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_TenantId_EmployeeNumber",
                table: "Employees",
                columns: new[] { "TenantId", "EmployeeNumber" },
                unique: true,
                filter: "\"EmployeeNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_TenantId_EntraObjectId",
                table: "Employees",
                columns: new[] { "TenantId", "EntraObjectId" },
                unique: true,
                filter: "\"EntraObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_TenantId",
                table: "AccessRequests",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_TicketCategoryGroups_TenantId_Category_EntraGroupId",
                table: "TicketCategoryGroups");

            migrationBuilder.DropIndex(
                name: "IX_Employees_TenantId_EmployeeNumber",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_Employees_TenantId_EntraObjectId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequests_TenantId",
                table: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TicketCategoryGroups");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "EmployeeResponsibilities");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AccessRequests");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategoryGroups_Category_EntraGroupId",
                table: "TicketCategoryGroups",
                columns: new[] { "Category", "EntraGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmployeeNumber",
                table: "Employees",
                column: "EmployeeNumber",
                unique: true,
                filter: "\"EmployeeNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EntraObjectId",
                table: "Employees",
                column: "EntraObjectId",
                unique: true,
                filter: "\"EntraObjectId\" IS NOT NULL");
        }
    }
}
