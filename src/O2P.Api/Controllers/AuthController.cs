using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [AllowAnonymous]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IConfiguration _config;

        public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, IConfiguration config)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _config = config;
        }

        public class LoginRequest
        {
            public string Username { get; set; } = null!;
            public string Password { get; set; } = null!;
        }

        public class ChangePasswordRequest
        {
            public string CurrentPassword { get; set; } = null!;
            public string NewPassword { get; set; } = null!;
        }

        public class PublicChangePasswordRequest
        {
            public string Username { get; set; } = null!;
            public string CurrentPassword { get; set; } = null!;
            public string NewPassword { get; set; } = null!;
        }

        [EnableRateLimiting("login")]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var user = await _userManager.FindByNameAsync(request.Username);
            if (user == null) return Unauthorized(new { message = "Invalid credentials" });

            if (!user.IsActive)
            {
                return Unauthorized(new { message = "This account is disabled." });
            }

            var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, true);
            if (result.IsLockedOut)
            {
                return StatusCode(423, new { message = "This account is temporarily locked due to repeated failed sign-in attempts." });
            }
            if (!result.Succeeded) return Unauthorized(new { message = "Invalid credentials" });

            user.LastLoginAt = DateTimeOffset.UtcNow;
            await _userManager.UpdateAsync(user);

            var roles = await _userManager.GetRolesAsync(user);
            var token = GenerateJwtToken(user, roles);
            return Ok(BuildAuthResponse(user, roles, token));
        }

        // Change password from the login page (no session token). Requires the current password,
        // shares the login rate limit and counts failures toward account lockout.
        [EnableRateLimiting("login")]
        [HttpPost("change-password-public")]
        public async Task<IActionResult> ChangePasswordPublic([FromBody] PublicChangePasswordRequest request)
        {
            var user = await _userManager.FindByNameAsync(request.Username ?? string.Empty);
            if (user == null || !user.IsActive) return Unauthorized(new { message = "Invalid credentials" });

            var signIn = await _signInManager.CheckPasswordSignInAsync(user, request.CurrentPassword, true);
            if (signIn.IsLockedOut)
            {
                return StatusCode(423, new { message = "This account is temporarily locked due to repeated failed sign-in attempts." });
            }
            if (!signIn.Succeeded) return Unauthorized(new { message = "Invalid credentials" });

            var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                return BadRequest(new
                {
                    message = "Password change failed: " + string.Join(" ", result.Errors.Select(e => e.Description)),
                    errors = result.Errors
                });
            }

            user.MustChangePassword = false;
            user.LastPasswordChangedAt = DateTimeOffset.UtcNow;
            await _userManager.UpdateSecurityStampAsync(user);
            await _userManager.UpdateAsync(user);

            return Ok(new { message = "Password updated. Please sign in with your new password." });
        }

        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var roles = await _userManager.GetRolesAsync(user);
            return Ok(new
            {
                userId = user.Id,
                username = user.UserName,
                email = user.Email,
                displayName = user.DisplayName,
                roles,
                mustChangePassword = user.MustChangePassword,
                isActive = user.IsActive,
                lastLoginAt = user.LastLoginAt
            });
        }

        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
            {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                return BadRequest(new
                {
                    message = "Password change failed.",
                    errors = result.Errors
                });
            }

            user.MustChangePassword = false;
            user.LastPasswordChangedAt = DateTimeOffset.UtcNow;
            await _userManager.UpdateSecurityStampAsync(user);
            await _userManager.UpdateAsync(user);

            var roles = await _userManager.GetRolesAsync(user);
            var token = GenerateJwtToken(user, roles);
            return Ok(BuildAuthResponse(user, roles, token));
        }

        private string GenerateJwtToken(ApplicationUser user, IEnumerable<string> roles)
        {
            var jwtSettings = _config.GetSection("JwtSettings");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Secret"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName ?? ""),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserName ?? ""),
                new Claim("security_stamp", user.SecurityStamp ?? string.Empty),
                new Claim("must_change_password", user.MustChangePassword.ToString().ToLowerInvariant()),
            };

            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var token = new JwtSecurityToken(
                issuer: jwtSettings["Issuer"],
                audience: jwtSettings["Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(double.Parse(jwtSettings["ExpiryMinutes"]!)),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private object BuildAuthResponse(ApplicationUser user, IEnumerable<string> roles, string token)
        {
            return new
            {
                token,
                userId = user.Id,
                username = user.UserName,
                email = user.Email,
                displayName = user.DisplayName,
                roles,
                mustChangePassword = user.MustChangePassword,
                isActive = user.IsActive,
                lastLoginAt = user.LastLoginAt
            };
        }
    }
}
