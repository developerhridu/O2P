using Microsoft.AspNetCore.Identity;
using System;

namespace O2P.Domain.Entities
{
    public class ApplicationUser : IdentityUser<Guid>
    {
        public string? DisplayName { get; set; }
        public bool IsActive { get; set; } = true;
        public bool MustChangePassword { get; set; } = false;
        public DateTimeOffset? LastLoginAt { get; set; }
        public DateTimeOffset? LastPasswordChangedAt { get; set; }
    }
}
