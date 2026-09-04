using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M1_DiscoveryAndManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "applications",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DefaultsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discovery_cache",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ConnectionId = table.Column<long>(type: "bigint", nullable: false),
                    Owner = table.Column<string>(type: "text", nullable: false),
                    TableName = table.Column<string>(type: "text", nullable: false),
                    NumRows = table.Column<long>(type: "bigint", nullable: true),
                    SegmentBytes = table.Column<long>(type: "bigint", nullable: true),
                    LobBytes = table.Column<long>(type: "bigint", nullable: true),
                    IsPartitioned = table.Column<bool>(type: "boolean", nullable: false),
                    IsIot = table.Column<bool>(type: "boolean", nullable: false),
                    RefreshedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_cache", x => x.Id);
                    table.ForeignKey(
                        name: "FK_discovery_cache_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "o2p",
                        principalTable: "connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application_connections",
                schema: "o2p",
                columns: table => new
                {
                    ApplicationId = table.Column<long>(type: "bigint", nullable: false),
                    Slot = table.Column<string>(type: "text", nullable: false),
                    ConnectionId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_connections", x => new { x.ApplicationId, x.Slot });
                    table.ForeignKey(
                        name: "FK_application_connections_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "o2p",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_application_connections_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "o2p",
                        principalTable: "connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manifests",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ApplicationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manifests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manifests_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "o2p",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manifest_tables",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ManifestId = table.Column<long>(type: "bigint", nullable: false),
                    Owner = table.Column<string>(type: "text", nullable: false),
                    TableName = table.Column<string>(type: "text", nullable: false),
                    Included = table.Column<bool>(type: "boolean", nullable: false),
                    WhereClause = table.Column<string>(type: "text", nullable: true),
                    ExcludedColumns = table.Column<string[]>(type: "text[]", nullable: true),
                    EstRows = table.Column<long>(type: "bigint", nullable: true),
                    EstBytes = table.Column<long>(type: "bigint", nullable: true),
                    HasLobs = table.Column<bool>(type: "boolean", nullable: false),
                    IsPartitioned = table.Column<bool>(type: "boolean", nullable: false),
                    IsIot = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manifest_tables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manifest_tables_manifests_ManifestId",
                        column: x => x.ManifestId,
                        principalSchema: "o2p",
                        principalTable: "manifests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_connections_ConnectionId",
                schema: "o2p",
                table: "application_connections",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_Name",
                schema: "o2p",
                table: "applications",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_cache_ConnectionId_Owner_TableName",
                schema: "o2p",
                table: "discovery_cache",
                columns: new[] { "ConnectionId", "Owner", "TableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manifest_tables_ManifestId",
                schema: "o2p",
                table: "manifest_tables",
                column: "ManifestId");

            migrationBuilder.CreateIndex(
                name: "IX_manifests_ApplicationId_Name_Version",
                schema: "o2p",
                table: "manifests",
                columns: new[] { "ApplicationId", "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_connections",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "discovery_cache",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "manifest_tables",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "manifests",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "applications",
                schema: "o2p");
        }
    }
}
