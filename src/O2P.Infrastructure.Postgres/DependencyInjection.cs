using Microsoft.Extensions.DependencyInjection;
using O2P.Application.Interfaces;
using O2P.Infrastructure.Postgres.Schema;
using O2P.Infrastructure.Postgres.Writer;
using O2P.Infrastructure.Postgres.Validation;

namespace O2P.Infrastructure.Postgres
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddPostgresInfrastructure(this IServiceCollection services)
        {
            services.AddScoped<IPostgresDdlExecutor, PostgresDdlExecutor>();
            services.AddScoped<IPostgresConstraintManager, PostgresConstraintManager>();
            services.AddScoped<IPostgresBinaryWriter, PostgresBinaryWriter>();
            services.AddScoped<ITargetCountExecutor, PostgresCountExecutor>();
            services.AddScoped<ITargetPreflightExecutor, PostgresPreflightExecutor>();
            return services;
        }
    }
}
