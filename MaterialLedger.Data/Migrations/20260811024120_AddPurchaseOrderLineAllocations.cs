using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderLineAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseOrderLineAllocations",
                schema: "material",
                columns: table => new
                {
                    PurchaseOrderLineAllocationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PurchaseOrderLineId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryLocationId = table.Column<long>(type: "bigint", nullable: false),
                    QuantityPlanned = table.Column<decimal>(type: "decimal(12,3)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLineAllocations", x => x.PurchaseOrderLineAllocationId);
                    table.CheckConstraint("CK_PurchaseOrderLineAllocation_QuantityPlanned", "[QuantityPlanned] = FLOOR([QuantityPlanned]) AND [QuantityPlanned] >= 1");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLineAllocations_InventoryLocations_InventoryLocationId",
                        column: x => x.InventoryLocationId,
                        principalSchema: "material",
                        principalTable: "InventoryLocations",
                        principalColumn: "InventoryLocationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLineAllocations_PurchaseOrderLines_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalSchema: "material",
                        principalTable: "PurchaseOrderLines",
                        principalColumn: "PurchaseOrderLineId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLineAllocations_InventoryLocationId",
                schema: "material",
                table: "PurchaseOrderLineAllocations",
                column: "InventoryLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLineAllocations_PurchaseOrderLineId",
                schema: "material",
                table: "PurchaseOrderLineAllocations",
                column: "PurchaseOrderLineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseOrderLineAllocations",
                schema: "material");
        }
    }
}
