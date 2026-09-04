using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M8_ChunkPlannerBounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BoundColumn",
                schema: "o2p",
                table: "chunk_logs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartitionName",
                schema: "o2p",
                table: "chunk_logs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoundColumn",
                schema: "o2p",
                table: "chunk_logs");

            migrationBuilder.DropColumn(
                name: "PartitionName",
                schema: "o2p",
                table: "chunk_logs");
        }
    }
}
