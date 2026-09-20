using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920120000_M12_TableStatQuality")]
    public partial class M12_TableStatQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Records HOW a table's figures were obtained, so the UI never presents an estimate as
            // fact. Row/size values themselves stay nullable: null means Oracle does not know, which
            // is different from zero.
            foreach (var table in new[] { "discovery_cache", "manifest_tables" })
            {
                migrationBuilder.AddColumn<bool>(
                    name: "SizeIsEstimate",
                    schema: "o2p",
                    table: table,
                    type: "boolean",
                    nullable: false,
                    defaultValue: false);

                migrationBuilder.AddColumn<System.DateTimeOffset>(
                    name: "RowsCountedAt",
                    schema: "o2p",
                    table: table,
                    type: "timestamp with time zone",
                    nullable: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "discovery_cache", "manifest_tables" })
            {
                migrationBuilder.DropColumn(name: "SizeIsEstimate", schema: "o2p", table: table);
                migrationBuilder.DropColumn(name: "RowsCountedAt", schema: "o2p", table: table);
            }
        }
    }
}
