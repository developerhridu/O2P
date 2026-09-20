using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920000000_M11_TargetTablePreExisted")]
    public partial class M11_TargetTablePreExisted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable so rows written before this migration read as "unknown" rather than
            // claiming O2P created a table it may well have reused.
            migrationBuilder.AddColumn<bool>(
                name: "TargetTablePreExisted",
                schema: "o2p",
                table: "table_runs",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetTablePreExisted",
                schema: "o2p",
                table: "table_runs");
        }
    }
}
