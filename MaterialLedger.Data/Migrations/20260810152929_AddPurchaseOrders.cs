using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PurchaseOrderLineId",
                schema: "material",
                table: "SheetLots",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                schema: "material",
                columns: table => new
                {
                    PurchaseOrderId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VendorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PurchaseOrderNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.PurchaseOrderId);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_AppUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLines",
                schema: "material",
                columns: table => new
                {
                    PurchaseOrderLineId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    MaterialSpecId = table.Column<long>(type: "bigint", nullable: false),
                    VendorDescription = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VendorPartNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QuantityOrdered = table.Column<decimal>(type: "decimal(12,3)", nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "decimal(12,3)", nullable: false),
                    UsableWidthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    UsableLengthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    QuotedDeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLines", x => x.PurchaseOrderLineId);
                    table.CheckConstraint("CK_PurchaseOrderLine_QuantityOrdered", "[QuantityOrdered] = FLOOR([QuantityOrdered]) AND [QuantityOrdered] >= 1");
                    table.CheckConstraint("CK_PurchaseOrderLine_QuantityReceived", "[QuantityReceived] >= 0 AND [QuantityReceived] = FLOOR([QuantityReceived]) AND [QuantityReceived] <= [QuantityOrdered]");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_MaterialSpecs_MaterialSpecId",
                        column: x => x.MaterialSpecId,
                        principalSchema: "material",
                        principalTable: "MaterialSpecs",
                        principalColumn: "MaterialSpecId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalSchema: "material",
                        principalTable: "PurchaseOrders",
                        principalColumn: "PurchaseOrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SheetLots_PurchaseOrderLineId",
                schema: "material",
                table: "SheetLots",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_MaterialSpecId",
                schema: "material",
                table: "PurchaseOrderLines",
                column: "MaterialSpecId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_PurchaseOrderId_LineNumber",
                schema: "material",
                table: "PurchaseOrderLines",
                columns: new[] { "PurchaseOrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CreatedBy",
                schema: "material",
                table: "PurchaseOrders",
                column: "CreatedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_SheetLots_PurchaseOrderLines_PurchaseOrderLineId",
                schema: "material",
                table: "SheetLots",
                column: "PurchaseOrderLineId",
                principalSchema: "material",
                principalTable: "PurchaseOrderLines",
                principalColumn: "PurchaseOrderLineId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SheetLots_PurchaseOrderLines_PurchaseOrderLineId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLines",
                schema: "material");

            migrationBuilder.DropTable(
                name: "PurchaseOrders",
                schema: "material");

            migrationBuilder.DropIndex(
                name: "IX_SheetLots_PurchaseOrderLineId",
                schema: "material",
                table: "SheetLots");

            migrationBuilder.DropColumn(
                name: "PurchaseOrderLineId",
                schema: "material",
                table: "SheetLots");
        }
    }
}
