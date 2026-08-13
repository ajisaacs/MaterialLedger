using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations;

public partial class AddAttachmentMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "ByteLength", schema: "material", table: "Attachments", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<string>(name: "OriginalFileName", schema: "material", table: "Attachments", type: "nvarchar(255)", maxLength: 255, nullable: false, defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ByteLength", schema: "material", table: "Attachments");
        migrationBuilder.DropColumn(name: "OriginalFileName", schema: "material", table: "Attachments");
    }
}
