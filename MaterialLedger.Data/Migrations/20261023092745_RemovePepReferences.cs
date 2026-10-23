using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePepReferences : Migration
    {
        // Text put in front of the PEP year and name copied into NestName on legacy holds.
        private const string LegacyNestNamePrefix = "PEP ";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep a readable reference on legacy holds before their PEP columns go away.
            // LEFT keeps a long legacy name within NestName's 200 characters.
            migrationBuilder.Sql($"""
                UPDATE [material].[MaterialReservations]
                SET [NestName] = LEFT(CASE
                        WHEN [PepNestYear] IS NULL THEN CONCAT(N'{LegacyNestNamePrefix}', [PepNestName])
                        ELSE CONCAT(N'{LegacyNestNamePrefix}', [PepNestYear], N': ', [PepNestName])
                    END, 200)
                WHERE [PepNestName] IS NOT NULL AND [NestName] IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "PepNestName",
                schema: "material",
                table: "MaterialReservations");

            migrationBuilder.DropColumn(
                name: "PepNestYear",
                schema: "material",
                table: "MaterialReservations");

            migrationBuilder.Sql("DELETE FROM [material].[MaterialSpecExternalMappings] WHERE [ExternalSystem] = N'PEP';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings",
                sql: "[ExternalSystem] IN (N'OpenNest')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the schema only. The deleted PEP mappings and the dropped PEP year/name values
            // cannot be recovered; legacy holds keep the copied text in NestName.
            migrationBuilder.DropCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings");

            migrationBuilder.AddColumn<string>(
                name: "PepNestName",
                schema: "material",
                table: "MaterialReservations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PepNestYear",
                schema: "material",
                table: "MaterialReservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings",
                sql: "[ExternalSystem] IN (N'PEP', N'OpenNest')");
        }
    }
}
