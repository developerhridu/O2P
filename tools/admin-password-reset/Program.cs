using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;

// Admin-level password reset for an O2P user (defaults to the bootstrap "admin"). Reuses the app's
// exact Identity configuration (AddMetadataInfrastructure) so the resulting password hash, security
// stamp, and policy checks match the running API. Also clears any lockout, so it recovers an account
// locked out by repeated failed sign-ins. Does NOT require the current password.
//
// Env vars:
//   ConnectionStrings__MetadataDb  (or O2P_METADATA_CONN) - metadata DB connection string  [required]
//   O2P_RESET_NEWPW                - the new password (must satisfy the policy)              [required]
//   O2P_RESET_USER                 - username to reset (default: admin)                      [optional]
var conn = Environment.GetEnvironmentVariable("ConnectionStrings__MetadataDb")
           ?? Environment.GetEnvironmentVariable("O2P_METADATA_CONN")
           ?? throw new InvalidOperationException("Set ConnectionStrings__MetadataDb or O2P_METADATA_CONN");
var newPassword = Environment.GetEnvironmentVariable("O2P_RESET_NEWPW")
           ?? throw new InvalidOperationException("O2P_RESET_NEWPW not set");
var username = Environment.GetEnvironmentVariable("O2P_RESET_USER") ?? "admin";

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:MetadataDb"] = conn })
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IConfiguration>(config);
services.AddMetadataInfrastructure(config);

await using var sp = services.BuildServiceProvider();
using var scope = sp.CreateScope();
var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

var user = await um.FindByNameAsync(username);
if (user is null)
{
    Console.WriteLine("RESET_RESULT=USER_NOT_FOUND");
    return 2;
}

// Clear any lockout first (the account may be locked from failed sign-ins).
await um.SetLockoutEndDateAsync(user, null);
await um.ResetAccessFailedCountAsync(user);

// Force-reset the password without needing the current one.
var token = await um.GeneratePasswordResetTokenAsync(user);
var result = await um.ResetPasswordAsync(user, token, newPassword);
if (!result.Succeeded)
{
    Console.WriteLine("RESET_RESULT=FAIL " + string.Join("; ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));
    return 3;
}

// Force a change on next login and invalidate any existing sessions/tokens.
user.MustChangePassword = true;
user.LastPasswordChangedAt = DateTimeOffset.UtcNow;
await um.UpdateSecurityStampAsync(user);
await um.UpdateAsync(user);

var refreshed = await um.FindByNameAsync(username);
Console.WriteLine($"RESET_RESULT=OK user={refreshed!.UserName} lockedOut={await um.IsLockedOutAsync(refreshed)} mustChange={refreshed.MustChangePassword}");
return 0;
