using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M7_AuthSecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "o2p",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "o2p",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLoginAt",
                schema: "o2p",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPasswordChangedAt",
                schema: "o2p",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "o2p",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "o2p",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "o2p",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                schema: "o2p",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastPasswordChangedAt",
                schema: "o2p",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "o2p",
                table: "users");
        }
    }
}
