using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M5_ValidationAndMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricSamples",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    JobRunId = table.Column<long>(type: "bigint", nullable: false),
                    TableRunId = table.Column<long>(type: "bigint", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowsPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    MbPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    ActiveChunkWorkers = table.Column<int>(type: "integer", nullable: false),
                    OracleSessions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricSamples_job_runs_JobRunId",
                        column: x => x.JobRunId,
                        principalSchema: "o2p",
                        principalTable: "job_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetricSamples_table_runs_TableRunId",
                        column: x => x.TableRunId,
                        principalSchema: "o2p",
                        principalTable: "table_runs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ValidationResults",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TableRunId = table.Column<long>(type: "bigint", nullable: false),
                    CheckKind = table.Column<string>(type: "text", nullable: false),
                    ColumnName = table.Column<string>(type: "text", nullable: true),
                    SourceValue = table.Column<string>(type: "text", nullable: false),
                    TargetValue = table.Column<string>(type: "text", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    DetailJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValidationResults_table_runs_TableRunId",
                        column: x => x.TableRunId,
                        principalSchema: "o2p",
                        principalTable: "table_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSamples_JobRunId",
                schema: "o2p",
                table: "MetricSamples",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricSamples_TableRunId",
                schema: "o2p",
                table: "MetricSamples",
                column: "TableRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationResults_TableRunId",
                schema: "o2p",
                table: "ValidationResults",
                column: "TableRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricSamples",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "ValidationResults",
                schema: "o2p");
        }
    }
}
