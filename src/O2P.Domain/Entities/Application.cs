using System;
using System.Collections.Generic;

namespace O2P.Domain.Entities
{
    public class Application
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public string? DefaultsJson { get; set; } // Default settings (JSONB)
        
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        
        // Navigation properties
        public ICollection<ApplicationConnection> Connections { get; set; } = new List<ApplicationConnection>();
        public ICollection<Manifest> Manifests { get; set; } = new List<Manifest>();
    }
}
