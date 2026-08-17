using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyPurchaseOrderVendorStrings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VendorName",
                schema: "material",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "VendorPartNumber",
                schema: "material",
                table: "PurchaseOrderLines");

            migrationBuilder.AlterColumn<long>(
                name: "VendorId",
                schema: "material",
                table: "PurchaseOrders",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "VendorId",
                schema: "material",
                table: "PurchaseOrders",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "VendorName",
                schema: "material",
                table: "PurchaseOrders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VendorPartNumber",
                schema: "material",
                table: "PurchaseOrderLines",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }
    }
}
