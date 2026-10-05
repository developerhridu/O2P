using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260921000000_M15_ChangeTracking")]
    public partial class M15_ChangeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Everything recorded at the start of a bulk copy so change tracking can continue from it.
            // Nullable throughout: runs from before this migration recorded none of it, and such a copy
            // simply cannot be tracked - it is never treated as if it had started at some guessed point.
            // SCNs are numeric, not bigint: Oracle documents them growing past 48 bits.
            migrationBuilder.AddColumn<bool>(
                name: "LoggingReadyAtStart",
                schema: "o2p",
                table: "table_runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RowsDeleted",
                schema: "o2p",
                table: "table_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RowsWritten",
                schema: "o2p",
                table: "table_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SourceKeyJson",
                schema: "o2p",
                table: "table_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceObjectIdsJson",
                schema: "o2p",
                table: "table_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SourceStartScn",
                schema: "o2p",
                table: "table_runs",
                type: "numeric",
                nullable: true);

            // Every existing run is a bulk run, which the default states correctly.
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "o2p",
                table: "job_runs",
                type: "text",
                nullable: false,
                defaultValue: "bulk");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                schema: "o2p",
                table: "job_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerId",
                schema: "o2p",
                table: "job_runs",
                type: "text",
                nullable: true);

            // No foreign keys, on purpose. Saving a table selection deletes and re-adds its tables, and
            // table runs cascade from those rows, so a tracker linked to either would vanish on an
            // ordinary save. It is keyed by the destination table, which is what is being kept in sync.
            migrationBuilder.CreateTable(
                name: "tracked_tables",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    SourceConnectionId = table.Column<long>(type: "bigint", nullable: false),
                    SourceOwner = table.Column<string>(type: "text", nullable: false),
                    SourceTable = table.Column<string>(type: "text", nullable: false),
                    ColumnsJson = table.Column<string>(type: "jsonb", nullable: false),
                    WhereClause = table.Column<string>(type: "text", nullable: true),
                    KeyColumnsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ObjectIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    TargetConnectionId = table.Column<long>(type: "bigint", nullable: false),
                    TargetSchema = table.Column<string>(type: "text", nullable: false),
                    TargetTableName = table.Column<string>(type: "text", nullable: false),
                    TargetNameStyle = table.Column<string>(type: "text", nullable: true),
                    LastScn = table.Column<decimal>(type: "numeric", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ActiveJobRunId = table.Column<long>(type: "bigint", nullable: true),
                    HeldBackByJson = table.Column<string>(type: "jsonb", nullable: true),
                    SetUpFromTableRunId = table.Column<long>(type: "bigint", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_tables", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_tables_SourceConnectionId_SourceOwner_SourceTable",
                schema: "o2p",
                table: "tracked_tables",
                columns: new[] { "SourceConnectionId", "SourceOwner", "SourceTable" });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_tables_TargetConnectionId_TargetSchema_TargetTableN~",
                schema: "o2p",
                table: "tracked_tables",
                columns: new[] { "TargetConnectionId", "TargetSchema", "TargetTableName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracked_tables",
                schema: "o2p");

            migrationBuilder.DropColumn(
                name: "LoggingReadyAtStart",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "RowsDeleted",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "RowsWritten",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "SourceKeyJson",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "SourceObjectIdsJson",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "SourceStartScn",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "o2p",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                schema: "o2p",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                schema: "o2p",
                table: "job_runs");
        }
    }
}
