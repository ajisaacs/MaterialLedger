using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenNestReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings");

            migrationBuilder.AddColumn<string>(
                name: "NestName",
                schema: "material",
                table: "MaterialReservations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenNestNestId",
                schema: "material",
                table: "MaterialReservations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings",
                sql: "[ExternalSystem] IN (N'PEP', N'OpenNest')");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialReservations_OpenNestNestId",
                schema: "material",
                table: "MaterialReservations",
                column: "OpenNestNestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings");

            migrationBuilder.DropIndex(
                name: "IX_MaterialReservations_OpenNestNestId",
                schema: "material",
                table: "MaterialReservations");

            migrationBuilder.DropColumn(
                name: "NestName",
                schema: "material",
                table: "MaterialReservations");

            migrationBuilder.DropColumn(
                name: "OpenNestNestId",
                schema: "material",
                table: "MaterialReservations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MaterialSpecExternalMapping_ExternalSystem",
                schema: "material",
                table: "MaterialSpecExternalMappings",
                sql: "[ExternalSystem] IN (N'PEP')");
        }
    }
}
