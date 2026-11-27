using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyBuddy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeckDocumentMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DocumentFormat",
                table: "Decks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SourcePageOrSlideCount",
                table: "Decks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsedOcr",
                table: "Decks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentFormat",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "SourcePageOrSlideCount",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "UsedOcr",
                table: "Decks");
        }
    }
}
