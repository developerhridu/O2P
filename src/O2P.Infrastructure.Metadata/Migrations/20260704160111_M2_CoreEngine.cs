using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M2_CoreEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_runs",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ApplicationId = table.Column<long>(type: "bigint", nullable: false),
                    ManifestId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SourceSlot = table.Column<string>(type: "text", nullable: false),
                    TargetSlot = table.Column<string>(type: "text", nullable: false),
                    TargetSchema = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_runs_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "o2p",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_job_runs_manifests_ManifestId",
                        column: x => x.ManifestId,
                        principalSchema: "o2p",
                        principalTable: "manifests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "table_runs",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    JobRunId = table.Column<long>(type: "bigint", nullable: false),
                    ManifestTableId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TargetTableName = table.Column<string>(type: "text", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowsMigrated = table.Column<long>(type: "bigint", nullable: false),
                    BytesMigrated = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_table_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_table_runs_job_runs_JobRunId",
                        column: x => x.JobRunId,
                        principalSchema: "o2p",
                        principalTable: "job_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_table_runs_manifest_tables_ManifestTableId",
                        column: x => x.ManifestTableId,
                        principalSchema: "o2p",
                        principalTable: "manifest_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunk_logs",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    TableRunId = table.Column<long>(type: "bigint", nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    StartRowId = table.Column<string>(type: "text", nullable: false),
                    EndRowId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    WorkerId = table.Column<string>(type: "text", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowsMigrated = table.Column<long>(type: "bigint", nullable: false),
                    BytesMigrated = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunk_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_chunk_logs_table_runs_TableRunId",
                        column: x => x.TableRunId,
                        principalSchema: "o2p",
                        principalTable: "table_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chunk_logs_LeaseExpiresAt",
                schema: "o2p",
                table: "chunk_logs",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_chunk_logs_Status",
                schema: "o2p",
                table: "chunk_logs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_chunk_logs_TableRunId",
                schema: "o2p",
                table: "chunk_logs",
                column: "TableRunId");

            migrationBuilder.CreateIndex(
                name: "IX_chunk_logs_WorkerId",
                schema: "o2p",
                table: "chunk_logs",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_ApplicationId",
                schema: "o2p",
                table: "job_runs",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_ManifestId",
                schema: "o2p",
                table: "job_runs",
                column: "ManifestId");

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_Status",
                schema: "o2p",
                table: "job_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_table_runs_JobRunId_TargetTableName",
                schema: "o2p",
                table: "table_runs",
                columns: new[] { "JobRunId", "TargetTableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_table_runs_ManifestTableId",
                schema: "o2p",
                table: "table_runs",
                column: "ManifestTableId");

            migrationBuilder.CreateIndex(
                name: "IX_table_runs_Status",
                schema: "o2p",
                table: "table_runs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunk_logs",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "table_runs",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "job_runs",
                schema: "o2p");
        }
    }
}
