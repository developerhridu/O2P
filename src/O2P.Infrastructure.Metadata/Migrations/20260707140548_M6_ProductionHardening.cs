using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M6_ProductionHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "row_rejects",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    TableRunId = table.Column<long>(type: "bigint", nullable: false),
                    ChunkLogId = table.Column<long>(type: "bigint", nullable: true),
                    SourceRowId = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ColumnName = table.Column<string>(type: "text", nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_row_rejects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "run_events",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    JobRunId = table.Column<long>(type: "bigint", nullable: true),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Event = table.Column<string>(type: "text", nullable: false),
                    DetailJson = table.Column<string>(type: "jsonb", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "run_logs",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    JobRunId = table.Column<long>(type: "bigint", nullable: true),
                    TableRunId = table.Column<long>(type: "bigint", nullable: true),
                    ChunkLogId = table.Column<long>(type: "bigint", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "target_name_allocations",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    TargetConnectionId = table.Column<long>(type: "bigint", nullable: false),
                    SchemaName = table.Column<string>(type: "text", nullable: false),
                    BaseName = table.Column<string>(type: "text", nullable: false),
                    SuffixNumber = table.Column<int>(type: "integer", nullable: false),
                    TableRunId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_target_name_allocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "type_mapping_rules",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    ApplicationId = table.Column<long>(type: "bigint", nullable: true),
                    ManifestTableId = table.Column<long>(type: "bigint", nullable: true),
                    ColumnName = table.Column<string>(type: "text", nullable: true),
                    MatchJson = table.Column<string>(type: "jsonb", nullable: false),
                    TargetType = table.Column<string>(type: "text", nullable: false),
                    OptionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_type_mapping_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_row_rejects_ChunkLogId",
                schema: "o2p",
                table: "row_rejects",
                column: "ChunkLogId");

            migrationBuilder.CreateIndex(
                name: "IX_row_rejects_TableRunId",
                schema: "o2p",
                table: "row_rejects",
                column: "TableRunId");

            migrationBuilder.CreateIndex(
                name: "IX_run_events_At",
                schema: "o2p",
                table: "run_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_run_events_JobRunId",
                schema: "o2p",
                table: "run_events",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_run_logs_JobRunId",
                schema: "o2p",
                table: "run_logs",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_run_logs_Timestamp",
                schema: "o2p",
                table: "run_logs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_target_name_allocations_TableRunId",
                schema: "o2p",
                table: "target_name_allocations",
                column: "TableRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_target_name_allocations_TargetConnectionId_SchemaName_BaseN~",
                schema: "o2p",
                table: "target_name_allocations",
                columns: new[] { "TargetConnectionId", "SchemaName", "BaseName", "SuffixNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_type_mapping_rules_Scope_ApplicationId_ManifestTableId_Colu~",
                schema: "o2p",
                table: "type_mapping_rules",
                columns: new[] { "Scope", "ApplicationId", "ManifestTableId", "ColumnName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "row_rejects",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "run_events",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "run_logs",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "target_name_allocations",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "type_mapping_rules",
                schema: "o2p");
        }
    }
}
