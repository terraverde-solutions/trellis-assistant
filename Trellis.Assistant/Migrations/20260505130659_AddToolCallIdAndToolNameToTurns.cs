using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trellis.Assistant.Migrations
{
    /// <inheritdoc />
    public partial class AddToolCallIdAndToolNameToTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tool_call_id",
                table: "turns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_name",
                table: "turns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tool_call_id",
                table: "turns");

            migrationBuilder.DropColumn(
                name: "tool_name",
                table: "turns");
        }
    }
}
