using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExpectedRemnants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpectedRemnants",
                schema: "material",
                columns: table => new
                {
                    ExpectedRemnantId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MaterialReservationId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    WidthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: false),
                    LengthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpectedRemnants", x => x.ExpectedRemnantId);
                    table.ForeignKey(
                        name: "FK_ExpectedRemnants_MaterialReservations_MaterialReservationId",
                        column: x => x.MaterialReservationId,
                        principalSchema: "material",
                        principalTable: "MaterialReservations",
                        principalColumn: "MaterialReservationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpectedRemnants_MaterialReservationId_Sequence",
                schema: "material",
                table: "ExpectedRemnants",
                columns: new[] { "MaterialReservationId", "Sequence" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO [material].[ExpectedRemnants] ([MaterialReservationId], [Sequence], [WidthIn], [LengthIn], [SourceType])
                SELECT [MaterialReservationId], 1, [ExpectedRemainderWidthIn], [ExpectedRemainderLengthIn], [ExpectedRemainderSourceType]
                FROM [material].[MaterialReservations]
                WHERE [ExpectedRemainderWidthIn] IS NOT NULL
                  AND [ExpectedRemainderLengthIn] IS NOT NULL
                  AND [ExpectedRemainderSourceType] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpectedRemnants",
                schema: "material");
        }
    }
}
