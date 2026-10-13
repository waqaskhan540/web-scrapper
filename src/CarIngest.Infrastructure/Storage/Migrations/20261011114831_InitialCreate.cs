using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace CarIngest.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "address_source",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location = table.Column<string>(type: "text", nullable: false),
                    last_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_successful_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_address_source", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    address_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    retry_of_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    files_read = table.Column<List<string>>(type: "text[]", nullable: false),
                    addresses_read = table.Column<int>(type: "integer", nullable: false),
                    stored_count = table.Column<int>(type: "integer", nullable: false),
                    unchanged_count = table.Column<int>(type: "integer", nullable: false),
                    skipped_by_policy_count = table.Column<int>(type: "integer", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_run", x => x.id);
                    table.CheckConstraint("ck_ingestion_run_status", "status IN ('running','succeeded','failed','interrupted')");
                    table.ForeignKey(
                        name: "FK_ingestion_run_address_source_address_source_id",
                        column: x => x.address_source_id,
                        principalTable: "address_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ingestion_run_ingestion_run_retry_of_run_id",
                        column: x => x.retry_of_run_id,
                        principalTable: "ingestion_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_target",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    address_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    discovery_method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    discovery_source_file = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    known_e_tag = table.Column<string>(type: "text", nullable: true),
                    known_last_modified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_target", x => x.id);
                    table.ForeignKey(
                        name: "FK_ingestion_target_address_source_address_source_id",
                        column: x => x.address_source_id,
                        principalTable: "address_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "address_outcome",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingestion_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingestion_target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    car_document_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_address_outcome", x => x.id);
                    table.CheckConstraint("ck_address_outcome_kind", "outcome IN ('stored','unchanged','skipped-by-policy','failed')");
                    table.ForeignKey(
                        name: "FK_address_outcome_ingestion_run_ingestion_run_id",
                        column: x => x.ingestion_run_id,
                        principalTable: "ingestion_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_address_outcome_ingestion_target_ingestion_target_id",
                        column: x => x.ingestion_target_id,
                        principalTable: "ingestion_target",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "car_document",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingestion_target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    make = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    trim = table.Column<string>(type: "text", nullable: true),
                    mileage_value = table.Column<int>(type: "integer", nullable: true),
                    mileage_unit = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    price_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    price_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    fuel_type = table.Column<string>(type: "text", nullable: true),
                    transmission = table.Column<string>(type: "text", nullable: true),
                    body_style = table.Column<string>(type: "text", nullable: true),
                    colour = table.Column<string>(type: "text", nullable: true),
                    vin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    availability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detected_language = table.Column<string>(type: "text", nullable: true),
                    content_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    extractor_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    discovery_method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_car_document", x => x.id);
                    table.CheckConstraint("ck_car_document_availability", "availability IN ('available','unavailable')");
                    table.CheckConstraint("ck_car_document_mileage", "mileage_value IS NULL OR (mileage_value BETWEEN 0 AND 1000000)");
                    table.CheckConstraint("ck_car_document_mileage_unit", "mileage_value IS NULL OR mileage_unit IS NOT NULL");
                    table.CheckConstraint("ck_car_document_price", "price_amount IS NULL OR (price_amount BETWEEN 0 AND 10000000)");
                    table.CheckConstraint("ck_car_document_price_currency", "price_amount IS NULL OR price_currency IS NOT NULL");
                    table.CheckConstraint("ck_car_document_year", "year BETWEEN 1900 AND 2100");
                    table.ForeignKey(
                        name: "FK_car_document_ingestion_target_ingestion_target_id",
                        column: x => x.ingestion_target_id,
                        principalTable: "ingestion_target",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "car_document_embedding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    car_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: false),
                    model_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    embedded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_text_hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_car_document_embedding", x => x.id);
                    table.ForeignKey(
                        name: "FK_car_document_embedding_car_document_car_document_id",
                        column: x => x.car_document_id,
                        principalTable: "car_document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_address_outcome_ingestion_run_id_ingestion_target_id",
                table: "address_outcome",
                columns: new[] { "ingestion_run_id", "ingestion_target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_address_outcome_ingestion_target_id",
                table: "address_outcome",
                column: "ingestion_target_id");

            migrationBuilder.CreateIndex(
                name: "IX_address_source_location",
                table: "address_source",
                column: "location",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_car_document_ingestion_target_id",
                table: "car_document",
                column: "ingestion_target_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_car_document_embedding_car_document_id_model_id_model_versi~",
                table: "car_document_embedding",
                columns: new[] { "car_document_id", "model_id", "model_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_car_document_embedding_model_id_model_version",
                table: "car_document_embedding",
                columns: new[] { "model_id", "model_version" });

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_run_retry_of_run_id",
                table: "ingestion_run",
                column: "retry_of_run_id");

            migrationBuilder.CreateIndex(
                name: "ux_ingestion_run_one_active_per_source",
                table: "ingestion_run",
                column: "address_source_id",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_target_address_source_id",
                table: "ingestion_target",
                column: "address_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_target_host",
                table: "ingestion_target",
                column: "host");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_target_url",
                table: "ingestion_target",
                column: "url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "address_outcome");

            migrationBuilder.DropTable(
                name: "car_document_embedding");

            migrationBuilder.DropTable(
                name: "ingestion_run");

            migrationBuilder.DropTable(
                name: "car_document");

            migrationBuilder.DropTable(
                name: "ingestion_target");

            migrationBuilder.DropTable(
                name: "address_source");
        }
    }
}
