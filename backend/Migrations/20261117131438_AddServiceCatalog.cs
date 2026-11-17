using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceCatalogItemId",
                table: "Tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServiceCatalogItemId",
                table: "AccessRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceCatalogItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultPriority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    EntraGroupId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceCatalogItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ServiceCatalogItemId",
                table: "Tickets",
                column: "ServiceCatalogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_ServiceCatalogItemId",
                table: "AccessRequests",
                column: "ServiceCatalogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceCatalogItems_TenantId_Name",
                table: "ServiceCatalogItems",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRequests_ServiceCatalogItems_ServiceCatalogItemId",
                table: "AccessRequests",
                column: "ServiceCatalogItemId",
                principalTable: "ServiceCatalogItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_ServiceCatalogItems_ServiceCatalogItemId",
                table: "Tickets",
                column: "ServiceCatalogItemId",
                principalTable: "ServiceCatalogItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessRequests_ServiceCatalogItems_ServiceCatalogItemId",
                table: "AccessRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_ServiceCatalogItems_ServiceCatalogItemId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "ServiceCatalogItems");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ServiceCatalogItemId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequests_ServiceCatalogItemId",
                table: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "ServiceCatalogItemId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ServiceCatalogItemId",
                table: "AccessRequests");
        }
    }
}
