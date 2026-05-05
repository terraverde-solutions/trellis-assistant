using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trellis.Assistant.Migrations
{
    /// <inheritdoc />
    public partial class AddModelColumnToConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "conversations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "mistral-small:24b");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model",
                table: "conversations");
        }
    }
}
