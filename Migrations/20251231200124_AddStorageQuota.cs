using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileManager.API.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StorageLimit",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UsedStorage",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageLimit",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "UsedStorage",
                table: "Users");
        }
    }
}
