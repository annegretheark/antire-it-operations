using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessRequestEmployee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmployeeId",
                table: "AccessRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_EmployeeId",
                table: "AccessRequests",
                column: "EmployeeId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRequests_Employees_EmployeeId",
                table: "AccessRequests",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessRequests_Employees_EmployeeId",
                table: "AccessRequests");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequests_EmployeeId",
                table: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                table: "AccessRequests");
        }
    }
}
