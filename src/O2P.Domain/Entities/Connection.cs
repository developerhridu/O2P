using System;
using O2P.Domain.Enums;

namespace O2P.Domain.Entities
{
    public class Connection
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public ConnectionKind Kind { get; set; }
        public string Host { get; set; } = null!;
        public int Port { get; set; }
        public string ServiceOrDb { get; set; } = null!;
        public string Username { get; set; } = null!;
        public byte[] SecretCiphertext { get; set; } = null!;
        public string? OptionsJson { get; set; } // JSONB in Postgres
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
