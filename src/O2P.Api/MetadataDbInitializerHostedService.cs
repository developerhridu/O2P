using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;

namespace O2P.Api;

/// <summary>
/// Runs EF migrations and bootstrap seeding after the web server starts listening,
/// so Swagger/UI can open even while the metadata DB is still coming up.
/// </summary>
public sealed class MetadataDbInitializerHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MetadataDbInitializerHostedService> _logger;

    public MetadataDbInitializerHostedService(
        IServiceProvider services,
        ILogger<MetadataDbInitializerHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => InitializeAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var services = scope.ServiceProvider;
        try
        {
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var db = services.GetRequiredService<AppDbContext>();
            var secretProtector = services.GetRequiredService<ISecretProtector>();
            var config = services.GetRequiredService<IConfiguration>();

            var migrated = false;
            for (var attempt = 1; attempt <= 30; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await db.Database.MigrateAsync(cancellationToken);
                    migrated = true;
                    break;
                }
                catch (Exception ex) when (attempt < 30)
                {
                    _logger.LogWarning(ex, "Metadata DB migrate attempt {Attempt}/30 failed; retrying...", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }

            if (!migrated)
            {
                _logger.LogError("Metadata DB migrate failed after 30 attempts. API is up but DB-backed endpoints will fail until Postgres is reachable.");
                return;
            }

            var roles = new[] { "Admin", "Operator", "Viewer" };
            foreach (var roleName in roles)
            {
                if (!await roleManager.RoleExistsAsync(roleName))
                {
                    await roleManager.CreateAsync(new ApplicationRole { Name = roleName });
                }
            }

            var bootstrapAdmin = config.GetSection("BootstrapAdmin");
            var bootstrapUsername = bootstrapAdmin["Username"] ?? "admin";
            var bootstrapEmail = bootstrapAdmin["Email"] ?? "admin@o2p.internal";
            var bootstrapPassword = bootstrapAdmin["Password"];
            var effectiveBootstrapPassword = string.IsNullOrWhiteSpace(bootstrapPassword)
                ? "AdminPassword123!"
                : bootstrapPassword;

            var adminUser = await userManager.FindByNameAsync(bootstrapUsername);
            if (adminUser == null)
            {
                adminUser = new ApplicationUser
                {
                    UserName = bootstrapUsername,
                    Email = bootstrapEmail,
                    DisplayName = "O2P Administrator",
                    EmailConfirmed = true,
                    LockoutEnabled = true,
                    IsActive = true,
                    MustChangePassword = true,
                    LastPasswordChangedAt = DateTimeOffset.UtcNow
                };
                var createAdminResult = await userManager.CreateAsync(adminUser, effectiveBootstrapPassword);
                if (createAdminResult.Succeeded)
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }
            else
            {
                // Only touch an existing admin while it still has the seed password. Once the admin
                // has changed it, restarts must leave the account (and MustChangePassword) alone.
                var usedDefaultBootstrapPassword = effectiveBootstrapPassword == "AdminPassword123!";
                if (await userManager.CheckPasswordAsync(adminUser, "AdminPassword123!"))
                {
                    if (usedDefaultBootstrapPassword)
                    {
                        if (!adminUser.MustChangePassword)
                        {
                            adminUser.MustChangePassword = true;
                            await userManager.UpdateAsync(adminUser);
                        }
                        _logger.LogWarning("Bootstrap admin is using the default fallback password. Set BootstrapAdmin__Password or O2P_ADMIN_PASSWORD immediately for public deployments.");
                    }
                    else
                    {
                        var resetToken = await userManager.GeneratePasswordResetTokenAsync(adminUser);
                        var resetResult = await userManager.ResetPasswordAsync(adminUser, resetToken, effectiveBootstrapPassword);
                        if (resetResult.Succeeded)
                        {
                            adminUser.MustChangePassword = true;
                            adminUser.LastPasswordChangedAt = DateTimeOffset.UtcNow;
                            await userManager.UpdateSecurityStampAsync(adminUser);
                            await userManager.UpdateAsync(adminUser);
                            _logger.LogInformation("Bootstrap admin password rotated away from the default seed password.");
                        }
                    }
                }
            }

            var connections = db.Connections.Where(c => c.SecretCiphertext != null).ToList();
            foreach (var connection in connections)
            {
                if (!secretProtector.IsProtected(connection.SecretCiphertext))
                {
                    var legacyPassword = secretProtector.Unprotect(connection.SecretCiphertext);
                    connection.SecretCiphertext = secretProtector.Protect(legacyPassword);
                    connection.UpdatedAt = DateTimeOffset.UtcNow;
                    db.RunEvents.Add(new RunEvent
                    {
                        Actor = "system",
                        Event = "connection.secret_backfilled",
                        DetailJson = $"{{\"connectionId\":{connection.Id}}}",
                        At = DateTimeOffset.UtcNow
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Metadata DB migrate/seed completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while seeding the database.");
        }
    }
}
