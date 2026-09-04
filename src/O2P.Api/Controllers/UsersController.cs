using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [Authorize(Roles = "Admin")]
    public class UsersController : ControllerBase
    {
        private static readonly string[] SupportedRoles = { "Admin", "Operator", "Viewer" };

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;

        public UsersController(UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager)
        {
            _userManager = userManager;
            _roleManager = roleManager;
        }

        public class CreateUserRequest
        {
            public string Username { get; set; } = null!;
            public string Email { get; set; } = null!;
            public string? DisplayName { get; set; }
            public string Password { get; set; } = null!;
            public string[] Roles { get; set; } = Array.Empty<string>();
            public bool MustChangePassword { get; set; } = true;
            public bool IsActive { get; set; } = true;
        }

        public class UpdateUserRequest
        {
            public string Email { get; set; } = null!;
            public string? DisplayName { get; set; }
            public string[] Roles { get; set; } = Array.Empty<string>();
            public bool MustChangePassword { get; set; }
            public bool IsActive { get; set; }
        }

        public class ResetPasswordRequest
        {
            public string NewPassword { get; set; } = null!;
            public bool MustChangePassword { get; set; } = true;
        }

        [HttpGet]
        public async Task<IActionResult> GetUsers()
        {
            var users = await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();
            var payload = new List<object>(users.Count);
            foreach (var user in users)
            {
                payload.Add(new
                {
                    id = user.Id,
                    username = user.UserName,
                    email = user.Email,
                    displayName = user.DisplayName,
                    roles = await _userManager.GetRolesAsync(user),
                    isActive = user.IsActive,
                    mustChangePassword = user.MustChangePassword,
                    accessFailedCount = user.AccessFailedCount,
                    lockoutEnabled = user.LockoutEnabled,
                    lockoutEnd = user.LockoutEnd,
                    lastLoginAt = user.LastLoginAt,
                    lastPasswordChangedAt = user.LastPasswordChangedAt
                });
            }

            return Ok(new
            {
                roles = SupportedRoles,
                users = payload
            });
        }

        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
        {
            var roles = NormalizeRoles(request.Roles);
            if (roles.Length == 0)
            {
                return BadRequest(new { message = "At least one role is required." });
            }

            var user = new ApplicationUser
            {
                UserName = request.Username.Trim(),
                Email = request.Email.Trim(),
                DisplayName = request.DisplayName?.Trim(),
                EmailConfirmed = true,
                LockoutEnabled = true,
                IsActive = request.IsActive,
                MustChangePassword = request.MustChangePassword,
                LastPasswordChangedAt = DateTimeOffset.UtcNow
            };

            var createResult = await _userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
            {
                return BadRequest(new
                {
                    message = "Failed to create user.",
                    errors = createResult.Errors
                });
            }

            await EnsureRolesExistAsync(roles);
            await _userManager.AddToRolesAsync(user, roles);

            return Ok(new { message = "User created successfully." });
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null) return NotFound(new { message = "User not found." });

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("sub");
            if (currentUserId == user.Id.ToString() && !request.IsActive)
            {
                return BadRequest(new { message = "You cannot disable your own account." });
            }

            var roles = NormalizeRoles(request.Roles);
            if (roles.Length == 0)
            {
                return BadRequest(new { message = "At least one role is required." });
            }

            user.Email = request.Email.Trim();
            user.DisplayName = request.DisplayName?.Trim();
            user.IsActive = request.IsActive;
            user.MustChangePassword = request.MustChangePassword;
            user.LockoutEnabled = true;

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                return BadRequest(new { message = "Failed to update user.", errors = updateResult.Errors });
            }

            await EnsureRolesExistAsync(roles);
            var existingRoles = await _userManager.GetRolesAsync(user);
            var removeResult = await _userManager.RemoveFromRolesAsync(user, existingRoles);
            if (!removeResult.Succeeded)
            {
                return BadRequest(new { message = "Failed to update roles.", errors = removeResult.Errors });
            }

            var addResult = await _userManager.AddToRolesAsync(user, roles);
            if (!addResult.Succeeded)
            {
                return BadRequest(new { message = "Failed to update roles.", errors = addResult.Errors });
            }

            await _userManager.UpdateSecurityStampAsync(user);
            return Ok(new { message = "User updated successfully." });
        }

        [HttpPost("{id:guid}/reset-password")]
        public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest request)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null) return NotFound(new { message = "User not found." });

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
            if (!result.Succeeded)
            {
                return BadRequest(new { message = "Failed to reset password.", errors = result.Errors });
            }

            user.MustChangePassword = request.MustChangePassword;
            user.LastPasswordChangedAt = DateTimeOffset.UtcNow;
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            await _userManager.UpdateSecurityStampAsync(user);
            await _userManager.UpdateAsync(user);

            return Ok(new { message = "Password reset successfully." });
        }

        [HttpPost("{id:guid}/unlock")]
        public async Task<IActionResult> UnlockUser(Guid id)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null) return NotFound(new { message = "User not found." });

            await _userManager.SetLockoutEndDateAsync(user, null);
            await _userManager.ResetAccessFailedCountAsync(user);
            return Ok(new { message = "User unlocked successfully." });
        }

        private static string[] NormalizeRoles(string[] roles)
        {
            return roles
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Select(role => role.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(role => SupportedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        private async Task EnsureRolesExistAsync(string[] roles)
        {
            foreach (var role in roles)
            {
                if (!await _roleManager.RoleExistsAsync(role))
                {
                    await _roleManager.CreateAsync(new ApplicationRole(role));
                }
            }
        }
    }
}
