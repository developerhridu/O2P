using Microsoft.Extensions.DependencyInjection;
using O2P.Application.Interfaces;
using O2P.Infrastructure.Oracle.Discovery;
using O2P.Infrastructure.Oracle.Reader;
using O2P.Infrastructure.Oracle.Validation;

namespace O2P.Infrastructure.Oracle
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddOracleInfrastructure(this IServiceCollection services)
        {
            services.AddScoped<IOracleDiscoveryService, OracleDiscoveryService>();
            services.AddScoped<IOracleDataReader, OracleDataReader>();
            services.AddScoped<IOracleChunkPlanner, OracleChunkPlanner>();
            services.AddScoped<ISourceCountExecutor, OracleCountExecutor>();
            services.AddScoped<ISourcePreflightExecutor, OraclePreflightExecutor>();
            return services;
        }
    }
}
