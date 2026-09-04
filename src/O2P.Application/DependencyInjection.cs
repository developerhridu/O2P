using Microsoft.Extensions.DependencyInjection;
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
            services.AddScoped<MigrationEngine>();
            return services;
        }
    }
}
