using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyBuddy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeckRawExtractedText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RawExtractedText",
                table: "Decks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RawExtractedText",
                table: "Decks");
        }
    }
}
