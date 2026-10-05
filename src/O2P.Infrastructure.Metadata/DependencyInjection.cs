using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using O2P.Domain.Entities;
using O2P.Application.Interfaces;
using O2P.Infrastructure.Metadata.Security;
using System;

namespace O2P.Infrastructure.Metadata
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddMetadataInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = WithKeepAlive(configuration.GetConnectionString("MetadataDb"));

            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

            services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
            services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();

            services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredLength = 12;
                options.Password.RequiredUniqueChars = 4;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.MaxFailedAccessAttempts = 5;

                options.User.RequireUniqueEmail = true;
            })
                .AddEntityFrameworkStores<AppDbContext>()
                .AddDefaultTokenProviders();

            services.AddDataProtection()
                .SetApplicationName("O2P")
                .PersistKeysToDbContext<AppDbContext>();

            return services;
        }

        /// <summary>
        /// The same keepalive settings as O2P.Infrastructure.Postgres.PostgresConnectionSettings (see there for
        /// why), for the metadata database, which may also sit behind a balancer in a deployment. Kept here
        /// because this project does not reference that one. Values already in the connection string win.
        /// </summary>
        private static string? WithKeepAlive(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;

            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            if (builder.KeepAlive <= 0) builder.KeepAlive = 20;
            builder.TcpKeepAlive = true;
            if (builder.TcpKeepAliveTime <= 0) builder.TcpKeepAliveTime = 20;
            if (builder.TcpKeepAliveInterval <= 0) builder.TcpKeepAliveInterval = 5;
            if (builder.ConnectionIdleLifetime >= 300) builder.ConnectionIdleLifetime = 30;
            return builder.ConnectionString;
        }
    }
}
