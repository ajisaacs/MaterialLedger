using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "VendorId",
                schema: "material",
                table: "SheetLots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VendorPartId",
                schema: "material",
                table: "SheetLots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VendorId",
                schema: "material",
                table: "PurchaseOrders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VendorPartId",
                schema: "material",
                table: "PurchaseOrderLines",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SheetLots_VendorId",
                schema: "material",
                table: "SheetLots",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetLots_VendorPartId",
                schema: "material",
                table: "SheetLots",
                column: "VendorPartId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_VendorId",
                schema: "material",
                table: "PurchaseOrders",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_VendorPartId",
                schema: "material",
                table: "PurchaseOrderLines",
                column: "VendorPartId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderLines_VendorParts_VendorPartId",
                schema: "material",
                table: "PurchaseOrderLines",
                column: "VendorPartId",
                principalSchema: "material",
                principalTable: "VendorParts",
                principalColumn: "VendorPartId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrders_Vendors_VendorId",
                schema: "material",
                table: "PurchaseOrders",
                column: "VendorId",
                principalSchema: "material",
                principalTable: "Vendors",
                principalColumn: "VendorId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SheetLots_VendorParts_VendorPartId",
                schema: "material",
                table: "SheetLots",
                column: "VendorPartId",
                principalSchema: "material",
                principalTable: "VendorParts",
                principalColumn: "VendorPartId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SheetLots_Vendors_VendorId",
                schema: "material",
                table: "SheetLots",
                column: "VendorId",
                principalSchema: "material",
                principalTable: "Vendors",
                principalColumn: "VendorId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrderLines_VendorParts_VendorPartId",
                schema: "material",
                table: "PurchaseOrderLines");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrders_Vendors_VendorId",
                schema: "material",
                table: "PurchaseOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_SheetLots_VendorParts_VendorPartId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropForeignKey(
                name: "FK_SheetLots_Vendors_VendorId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropIndex(
                name: "IX_SheetLots_VendorId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropIndex(
                name: "IX_SheetLots_VendorPartId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_VendorId",
                schema: "material",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderLines_VendorPartId",
                schema: "material",
                table: "PurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "VendorId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropColumn(
                name: "VendorPartId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropColumn(
                name: "VendorId",
                schema: "material",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "VendorPartId",
                schema: "material",
                table: "PurchaseOrderLines");
        }
    }
}
