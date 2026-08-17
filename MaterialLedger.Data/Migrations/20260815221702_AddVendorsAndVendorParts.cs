using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorsAndVendorParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Vendors",
                schema: "material",
                columns: table => new
                {
                    VendorId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, computedColumnSql: "UPPER(RTRIM(LTRIM([Name])))", stored: true),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.VendorId);
                    table.CheckConstraint("CK_Vendor_Name_NotBlank", "LEN(LTRIM(RTRIM([Name]))) > 0");
                });

            migrationBuilder.CreateTable(
                name: "VendorParts",
                schema: "material",
                columns: table => new
                {
                    VendorPartId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VendorId = table.Column<long>(type: "bigint", nullable: false),
                    PartNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedPartNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, computedColumnSql: "UPPER(RTRIM(LTRIM([PartNumber])))", stored: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MinimumOrderQty = table.Column<decimal>(type: "decimal(12,3)", nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorParts", x => x.VendorPartId);
                    table.CheckConstraint("CK_VendorPart_PartNumber_NotBlank", "LEN(LTRIM(RTRIM([PartNumber]))) > 0");
                    table.ForeignKey(
                        name: "FK_VendorParts_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalSchema: "material",
                        principalTable: "Vendors",
                        principalColumn: "VendorId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorParts_VendorId_NormalizedPartNumber",
                schema: "material",
                table: "VendorParts",
                columns: new[] { "VendorId", "NormalizedPartNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_NormalizedName",
                schema: "material",
                table: "Vendors",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorParts",
                schema: "material");

            migrationBuilder.DropTable(
                name: "Vendors",
                schema: "material");
        }
    }
}
