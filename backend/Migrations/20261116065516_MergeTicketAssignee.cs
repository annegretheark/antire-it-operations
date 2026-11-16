using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class MergeTicketAssignee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Koble fritekst til ansatt når nøyaktig én ansatt matcher
            //    på fullt navn eller e-post (uten hensyn til store/små bokstaver)
            migrationBuilder.Sql("""
                UPDATE "Tickets" t
                SET "AssignedToEmployeeId" = m."EmployeeId"
                FROM (
                    SELECT t2."Id" AS "TicketId", MIN(e."Id") AS "EmployeeId"
                    FROM "Tickets" t2
                    JOIN "Employees" e
                      ON lower(e."FirstName" || ' ' || e."LastName") = lower(trim(t2."AssignedTo"))
                      OR lower(e."Email") = lower(trim(t2."AssignedTo"))
                    WHERE t2."AssignedTo" IS NOT NULL
                      AND t2."AssignedToEmployeeId" IS NULL
                    GROUP BY t2."Id"
                    HAVING COUNT(DISTINCT e."Id") = 1
                ) m
                WHERE t."Id" = m."TicketId";
                """);

            // 2. Fritekst som ikke kunne kobles bevares i beskrivelsen
            migrationBuilder.Sql("""
                UPDATE "Tickets"
                SET "Description" = "Description"
                    || E'\n\n[Ansvarlig (tidligere fritekst): ' || trim("AssignedTo") || ']'
                WHERE "AssignedTo" IS NOT NULL
                  AND trim("AssignedTo") <> ''
                  AND "AssignedToEmployeeId" IS NULL;
                """);

            // 3. Fjern det gamle fritekstfeltet
            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "Tickets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "Tickets",
                type: "text",
                nullable: true);
        }
    }
}
