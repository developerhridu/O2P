using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260921120000_M16_ChunkRetryAfter")]
    public partial class M16_ChunkRetryAfter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A batch that failed for a reason that can pass (dropped connection, stall) goes back to Pending
            // and is not claimed again before this time. Null for every existing batch: claim whenever.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetryAfter",
                schema: "o2p",
                table: "chunk_logs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetryAfter",
                schema: "o2p",
                table: "chunk_logs");
        }
    }
}
