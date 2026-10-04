using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M18_DashboardChangeCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "ManifestTableId",
                schema: "o2p",
                table: "table_runs",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "SourceOwner",
                schema: "o2p",
                table: "table_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTable",
                schema: "o2p",
                table: "table_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TrackedTableId",
                schema: "o2p",
                table: "table_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "ManifestId",
                schema: "o2p",
                table: "job_runs",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "ApplicationId",
                schema: "o2p",
                table: "job_runs",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "SourceConnectionId",
                schema: "o2p",
                table: "job_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceConnectionName",
                schema: "o2p",
                table: "job_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TargetConnectionId",
                schema: "o2p",
                table: "job_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetConnectionName",
                schema: "o2p",
                table: "job_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceOwner",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "SourceTable",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "TrackedTableId",
                schema: "o2p",
                table: "table_runs");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                schema: "o2p",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "SourceConnectionName",
                schema: "o2p",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                schema: "o2p",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "TargetConnectionName",
                schema: "o2p",
                table: "job_runs");

            migrationBuilder.AlterColumn<long>(
                name: "ManifestTableId",
                schema: "o2p",
                table: "table_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "ManifestId",
                schema: "o2p",
                table: "job_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "ApplicationId",
                schema: "o2p",
                table: "job_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
