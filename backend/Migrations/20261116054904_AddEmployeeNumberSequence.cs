using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <summary>
    /// Oppretter sekvensen som EmployeesController bruker til ansattnummer.
    /// Sekvensen ble tidligere opprettet manuelt, så migreringen gjør ingenting
    /// hvis den allerede finnes. Nye sekvenser starter over høyeste eksisterende
    /// numeriske ansattnummer (minst 1000).
    /// </summary>
    public partial class AddEmployeeNumberSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relkind = 'S' AND relname = 'employee_number_seq'
                    ) THEN
                        CREATE SEQUENCE employee_number_seq START WITH 1000;

                        PERFORM setval(
                            'employee_number_seq',
                            GREATEST(
                                1000,
                                COALESCE((
                                    SELECT MAX("EmployeeNumber"::bigint) + 1
                                    FROM "Employees"
                                    WHERE "EmployeeNumber" ~ '^[0-9]{1,18}$'
                                ), 0)
                            ),
                            false);
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bevisst tom: sekvensen kan ha eksistert før denne migreringen,
            // og å slette den ville ødelagt opprettelse av ansatte.
        }
    }
}
