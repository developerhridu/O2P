using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using O2P.Infrastructure.Metadata;
using O2P.Infrastructure.Oracle;
using O2P.Infrastructure.Postgres;
using O2P.Application;
using Serilog;
using System.IO;
using System.Text;
using System.Threading.RateLimiting;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog (console + rolling file under repo logs/)
builder.Host.UseSerilog((context, config) =>
{
    var logDir = Path.GetFullPath(Path.Combine(context.HostingEnvironment.ContentRootPath, "..", "..", "logs"));
    Directory.CreateDirectory(logDir);

    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.FromLogContext()
          .Enrich.WithProperty("Application", "O2P.Api")
          .WriteTo.Console()
          .WriteTo.File(
              path: Path.Combine(logDir, "o2p-api-.log"),
              rollingInterval: RollingInterval.Day,
              retainedFileCountLimit: 14,
              shared: true,
              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({Application}) {Message:lj}{NewLine}{Exception}");
});

// Add Metadata Infrastructure (EF Core, Identity, Data Protection)
builder.Services.AddMetadataInfrastructure(builder.Configuration);
builder.Services.AddOracleInfrastructure();
builder.Services.AddPostgresInfrastructure();
builder.Services.AddApplication();

// Configure JWT Authentication
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var jwtSecret = jwtSettings["Secret"] ?? "CHANGE-ME-O2P-DEVELOPMENT-SECRET-MUST-BE-OVERRIDDEN";
var secretKey = Encoding.UTF8.GetBytes(jwtSecret);
var securitySection = builder.Configuration.GetSection("Security");
var corsOrigins = securitySection.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[]
{
    "http://localhost:3000",
    "http://127.0.0.1:3000",
    "http://localhost:3051",
    "http://127.0.0.1:3051",
    "http://localhost:5151",
    "http://127.0.0.1:5151",
    "http://27.147.159.194:3051"
};

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidAudience = jwtSettings["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(secretKey),
        NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.UniqueName,
        RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var userManager = context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<O2P.Domain.Entities.ApplicationUser>>();
            var subject = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var tokenSecurityStamp = context.Principal?.FindFirst("security_stamp")?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                context.Fail("Missing token subject.");
                return;
            }

            var user = await userManager.FindByIdAsync(subject);
            if (user == null || !user.IsActive)
            {
                context.Fail("User account is unavailable.");
                return;
            }

            if (!string.Equals(tokenSecurityStamp, user.SecurityStamp, StringComparison.Ordinal))
            {
                context.Fail("Token is no longer valid.");
            }
        }
    };
});

builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddControllers(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new AuthorizeFilter(policy));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Enable CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Seed Identity Roles and Admin User
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var roleManager = services.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<O2P.Domain.Entities.ApplicationRole>>();
        var userManager = services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<O2P.Domain.Entities.ApplicationUser>>();
        var db = services.GetRequiredService<AppDbContext>();
        var secretProtector = services.GetRequiredService<O2P.Application.Interfaces.ISecretProtector>();
        var config = services.GetRequiredService<IConfiguration>();
        var logger = services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        var roles = new[] { "Admin", "Operator", "Viewer" };
        foreach (var roleName in roles)
        {
            var roleExists = await roleManager.RoleExistsAsync(roleName);
            if (!roleExists)
            {
                await roleManager.CreateAsync(new O2P.Domain.Entities.ApplicationRole { Name = roleName });
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
            adminUser = new O2P.Domain.Entities.ApplicationUser
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
            var usedDefaultBootstrapPassword = effectiveBootstrapPassword == "AdminPassword123!";
            if (usedDefaultBootstrapPassword)
            {
                adminUser.MustChangePassword = true;
                await userManager.UpdateAsync(adminUser);
                logger.LogWarning("Bootstrap admin is using the default fallback password. Set BootstrapAdmin__Password or O2P_ADMIN_PASSWORD immediately for public deployments.");
            }
            else if (await userManager.CheckPasswordAsync(adminUser, "AdminPassword123!"))
            {
                var resetToken = await userManager.GeneratePasswordResetTokenAsync(adminUser);
                var resetResult = await userManager.ResetPasswordAsync(adminUser, resetToken, effectiveBootstrapPassword);
                if (resetResult.Succeeded)
                {
                    adminUser.MustChangePassword = true;
                    adminUser.LastPasswordChangedAt = DateTimeOffset.UtcNow;
                    await userManager.UpdateSecurityStampAsync(adminUser);
                    await userManager.UpdateAsync(adminUser);
                    logger.LogInformation("Bootstrap admin password rotated away from the default seed password.");
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
                db.RunEvents.Add(new O2P.Domain.Entities.RunEvent
                {
                    Actor = "system",
                    Event = "connection.secret_backfilled",
                    DetailJson = $"{{\"connectionId\":{connection.Id}}}",
                    At = DateTimeOffset.UtcNow
                });
            }
        }
        await db.SaveChangesAsync();
    }
    catch (System.Exception ex)
    {
        var logger = services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
        logger.LogError(ex, "An error occurred while seeding the database.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    if (securitySection.GetValue<bool>("RequireHttps"))
    {
        app.UseHttpsRedirection();
    }
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.UseCors("Frontend");
app.UseRateLimiter();
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

try
{
    Log.Information("O2P.Api starting; file logs under ../../logs/o2p-api-*.log");
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
