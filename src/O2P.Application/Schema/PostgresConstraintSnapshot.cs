using System.Collections.Generic;
using System.Text.Json;

namespace O2P.Application.Schema
{
    public sealed class PostgresConstraintSnapshot
    {
        public string SchemaName { get; set; } = null!;
        public string TableName { get; set; } = null!;
        public List<PostgresConstraintEntry> Constraints { get; set; } = new();

        public static string Serialize(PostgresConstraintSnapshot snapshot) =>
            JsonSerializer.Serialize(snapshot);

        public static PostgresConstraintSnapshot? Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<PostgresConstraintSnapshot>(json);
        }
    }

    public sealed class PostgresConstraintEntry
    {
        /// <summary>Postgres contype: p=primary, u=unique, f=foreign, c=check.</summary>
        public string ConstraintType { get; set; } = null!;
        public string ConstraintName { get; set; } = null!;
        public bool IsInbound { get; set; }
        /// <summary>Schema of the table that owns this constraint (local or referencing).</summary>
        public string OwnerSchema { get; set; } = null!;
        /// <summary>Table that owns this constraint.</summary>
        public string OwnerTable { get; set; } = null!;
        public string Definition { get; set; } = null!;
        public string DropSql { get; set; } = null!;
        public string CreateSql { get; set; } = null!;
    }
}
