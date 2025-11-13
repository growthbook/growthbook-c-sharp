using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrowthBook.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers GrowthBook services using a configuration delegate to set up the base Context.
        /// - Registers a singleton GrowthBookFactory built from the configured Context
        /// - Registers a scoped IGrowthBook created from the factory for the current scope (no user-specific attributes)
        /// Consumers needing per-user attributes should inject GrowthBookFactory and call CreateForUser(...)
        /// </summary>
        public static IServiceCollection AddGrowthBook(this IServiceCollection services, Action<Context> configureContext)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configureContext == null) throw new ArgumentNullException(nameof(configureContext));

            services.AddSingleton<GrowthBookFactory>(provider =>
            {
                var baseContext = new Context();
                configureContext(baseContext);

                // If not provided, use the application's ILoggerFactory
                if (baseContext.LoggerFactory == null)
                {
                    baseContext.LoggerFactory = provider.GetService<ILoggerFactory>();
                }

                return new GrowthBookFactory(baseContext);
            });

            services.AddScoped<IGrowthBook>(provider =>
            {
                var factory = provider.GetRequiredService<GrowthBookFactory>();
                // Default IGrowthBook instance for the scope (no user-specific attributes)
                return factory.CreateForUser(new Dictionary<string, object>());
            });

            return services;
        }

        /// <summary>
        /// Registers GrowthBook services using a pre-built base Context.
        /// </summary>
        public static IServiceCollection AddGrowthBook(this IServiceCollection services, Context baseContext)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (baseContext == null) throw new ArgumentNullException(nameof(baseContext));

            services.AddSingleton<GrowthBookFactory>(provider =>
            {
                // If not provided, use the application's ILoggerFactory
                if (baseContext.LoggerFactory == null)
                {
                    baseContext.LoggerFactory = provider.GetService<ILoggerFactory>();
                }

                return new GrowthBookFactory(baseContext);
            });

            services.AddScoped<IGrowthBook>(provider =>
            {
                var factory = provider.GetRequiredService<GrowthBookFactory>();
                return factory.CreateForUser(new Dictionary<string, object>());
            });

            return services;
        }
    }
}
