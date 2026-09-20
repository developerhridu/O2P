using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920160000_M14_TargetNameStyle")]
    public partial class M14_TargetNameStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Which spelling a run uses for destination column names: "lower" for a table O2P created
            // (or found already in lower case), "source" for one an earlier run left behind under
            // Oracle's upper-case spelling.
            //
            // Nullable with no default on purpose: every row written before this migration belongs to
            // a run against an upper-case table, and PostgresName reads null as "source", so nothing
            // already recorded changes meaning. Backfilling "source" would say the same thing while
            // pretending the style had been chosen deliberately.
            migrationBuilder.AddColumn<string>(
                name: "TargetNameStyle",
                schema: "o2p",
                table: "table_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetNameStyle",
                schema: "o2p",
                table: "table_runs");
        }
    }
}
