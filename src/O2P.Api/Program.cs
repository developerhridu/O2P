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
using Microsoft.OpenApi.Models;
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
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "O2P API",
        Version = "v1",
        Description = "Oracle to PostgreSQL migration control plane"
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT from POST /api/v1/auth/login (Authorize value: the token only)."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddHostedService<O2P.Api.MetadataDbInitializerHostedService>();

// Enable CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "O2P API v1");
        options.RoutePrefix = "swagger";
    });

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        const string swaggerUrl = "http://localhost:5000/swagger";
        Log.Information("Swagger UI available at {SwaggerUrl}", swaggerUrl);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{swaggerUrl}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open Swagger in the browser. Open {SwaggerUrl} manually.", swaggerUrl);
        }
    });
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
