using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using O2P.Application.Interfaces;
using O2P.Application.Validation;
using O2P.Application.Core;

namespace O2P.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddScoped<IValidationService, ValidationService>();
            services.AddScoped<IPreflightValidatorService, PreflightValidatorService>();
            // Defaults unless the host registered its own first (the Worker binds them from configuration).
            services.TryAddSingleton(new O2P.Application.Copying.CopyTuningOptions());
            services.AddScoped<MigrationEngine>();
            services.AddScoped<ChangeRunEngine>();
            return services;
        }
    }
}
