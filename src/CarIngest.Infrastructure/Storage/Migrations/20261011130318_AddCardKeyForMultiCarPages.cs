using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarIngest.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddCardKeyForMultiCarPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_car_document_ingestion_target_id",
                table: "car_document");

            migrationBuilder.AddColumn<string>(
                name: "card_key",
                table: "car_document",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "detail_url",
                table: "car_document",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_car_document_ingestion_target_id_card_key",
                table: "car_document",
                columns: new[] { "ingestion_target_id", "card_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_car_document_ingestion_target_id_card_key",
                table: "car_document");

            migrationBuilder.DropColumn(
                name: "card_key",
                table: "car_document");

            migrationBuilder.DropColumn(
                name: "detail_url",
                table: "car_document");

            migrationBuilder.CreateIndex(
                name: "IX_car_document_ingestion_target_id",
                table: "car_document",
                column: "ingestion_target_id",
                unique: true);
        }
    }
}
