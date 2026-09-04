using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace O2P.Infrastructure.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class M3_SchemaMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RefreshedAt",
                schema: "o2p",
                table: "discovery_cache",
                newName: "LastRefreshedAt");

            migrationBuilder.CreateTable(
                name: "discovery_column_cache",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    DiscoveryCacheId = table.Column<long>(type: "bigint", nullable: false),
                    ColumnName = table.Column<string>(type: "text", nullable: false),
                    ColumnId = table.Column<int>(type: "integer", nullable: false),
                    DataType = table.Column<string>(type: "text", nullable: false),
                    DataLength = table.Column<int>(type: "integer", nullable: true),
                    DataPrecision = table.Column<int>(type: "integer", nullable: true),
                    DataScale = table.Column<int>(type: "integer", nullable: true),
                    IsNullable = table.Column<bool>(type: "boolean", nullable: false),
                    IsIdentity = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_column_cache", x => x.Id);
                    table.ForeignKey(
                        name: "FK_discovery_column_cache_discovery_cache_DiscoveryCacheId",
                        column: x => x.DiscoveryCacheId,
                        principalSchema: "o2p",
                        principalTable: "discovery_cache",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manifest_columns",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ManifestTableId = table.Column<long>(type: "bigint", nullable: false),
                    ColumnName = table.Column<string>(type: "text", nullable: false),
                    OracleDataType = table.Column<string>(type: "text", nullable: false),
                    PostgresDataType = table.Column<string>(type: "text", nullable: false),
                    IsNullable = table.Column<bool>(type: "boolean", nullable: false),
                    IsPrimaryKey = table.Column<bool>(type: "boolean", nullable: false),
                    IsExcluded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manifest_columns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manifest_columns_manifest_tables_ManifestTableId",
                        column: x => x.ManifestTableId,
                        principalSchema: "o2p",
                        principalTable: "manifest_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manifest_indexes",
                schema: "o2p",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ManifestTableId = table.Column<long>(type: "bigint", nullable: false),
                    IndexName = table.Column<string>(type: "text", nullable: false),
                    IsUnique = table.Column<bool>(type: "boolean", nullable: false),
                    Columns = table.Column<string>(type: "text", nullable: false),
                    IndexType = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manifest_indexes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manifest_indexes_manifest_tables_ManifestTableId",
                        column: x => x.ManifestTableId,
                        principalSchema: "o2p",
                        principalTable: "manifest_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_column_cache_DiscoveryCacheId_ColumnName",
                schema: "o2p",
                table: "discovery_column_cache",
                columns: new[] { "DiscoveryCacheId", "ColumnName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manifest_columns_ManifestTableId_ColumnName",
                schema: "o2p",
                table: "manifest_columns",
                columns: new[] { "ManifestTableId", "ColumnName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manifest_indexes_ManifestTableId_IndexName",
                schema: "o2p",
                table: "manifest_indexes",
                columns: new[] { "ManifestTableId", "IndexName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discovery_column_cache",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "manifest_columns",
                schema: "o2p");

            migrationBuilder.DropTable(
                name: "manifest_indexes",
                schema: "o2p");

            migrationBuilder.RenameColumn(
                name: "LastRefreshedAt",
                schema: "o2p",
                table: "discovery_cache",
                newName: "RefreshedAt");
        }
    }
}
