using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketEmployeeRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ReportedByEmployeeId",
                table: "Tickets",
                column: "ReportedByEmployeeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Employees_ReportedByEmployeeId",
                table: "Tickets",
                column: "ReportedByEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Employees_ReportedByEmployeeId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ReportedByEmployeeId",
                table: "Tickets");
        }
    }
}
