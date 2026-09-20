using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920140000_M13_WorkerHeartbeats")]
    public partial class M13_WorkerHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One row per running Worker process. The API reads it to tell the user whether anything
            // is processing runs; without it a queued run waits silently when no Worker is running.
            migrationBuilder.CreateTable(
                name: "worker_heartbeats",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    InstanceId = table.Column<string>(type: "text", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    ProcessId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_heartbeats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_worker_heartbeats_InstanceId",
                schema: "o2p",
                table: "worker_heartbeats",
                column: "InstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_worker_heartbeats_LastSeenAt",
                schema: "o2p",
                table: "worker_heartbeats",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "worker_heartbeats", schema: "o2p");
        }
    }
}
