using System;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrowthBook.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="GrowthBookClient"/> as a singleton using a configuration delegate.
        /// Calls <see cref="GrowthBookClient.InitializeAsync"/> synchronously at startup.
        /// </summary>
        public static IServiceCollection AddGrowthBookClient(this IServiceCollection services,
            Action<Options> configureOptions)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configureOptions == null) throw new ArgumentNullException(nameof(configureOptions));

            services.AddSingleton<GrowthBookClient>(provider =>
            {
                var options = new Options();
                configureOptions(options);

                if (options.LoggerFactory == null)
                    options.LoggerFactory = provider.GetService<ILoggerFactory>();

                var client = new GrowthBookClient(options);
                client.InitializeAsync().GetAwaiter().GetResult();
                return client;
            });

            return services;
        }

        /// <summary>
        /// Registers <see cref="GrowthBookClient"/> as a singleton using a pre-built <see cref="Options"/> instance.
        /// Calls <see cref="GrowthBookClient.InitializeAsync"/> synchronously at startup.
        /// </summary>
        public static IServiceCollection AddGrowthBookClient(this IServiceCollection services, Options options)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (options == null) throw new ArgumentNullException(nameof(options));

            services.AddSingleton<GrowthBookClient>(provider =>
            {
                if (options.LoggerFactory == null)
                    options.LoggerFactory = provider.GetService<ILoggerFactory>();

                var client = new GrowthBookClient(options);
                client.InitializeAsync().GetAwaiter().GetResult();
                return client;
            });

            return services;
        }

        /// <summary>
        /// Registers GrowthBook services using the legacy <see cref="GrowthBookFactory"/> API.
        /// </summary>
        [Obsolete("Use AddGrowthBookClient instead. GrowthBookFactory is deprecated.")]
        public static IServiceCollection AddGrowthBook(this IServiceCollection services,
            Action<Context> configureContext)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configureContext == null) throw new ArgumentNullException(nameof(configureContext));

            services.AddSingleton<GrowthBookFactory>(provider =>
            {
                var baseContext = new Context();
                configureContext(baseContext);

                if (baseContext.LoggerFactory == null)
                    baseContext.LoggerFactory = provider.GetService<ILoggerFactory>();

                return new GrowthBookFactory(baseContext);
            });

            services.AddScoped<IGrowthBook>(provider =>
            {
                var factory = provider.GetRequiredService<GrowthBookFactory>();
                return factory.CreateForUser(new System.Collections.Generic.Dictionary<string, object>());
            });

            return services;
        }

        /// <summary>
        /// Registers GrowthBook services using the legacy <see cref="GrowthBookFactory"/> API.
        /// </summary>
        [Obsolete("Use AddGrowthBookClient instead. GrowthBookFactory is deprecated.")]
        public static IServiceCollection AddGrowthBook(this IServiceCollection services, Context baseContext)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (baseContext == null) throw new ArgumentNullException(nameof(baseContext));

            services.AddSingleton<GrowthBookFactory>(provider =>
            {
                if (baseContext.LoggerFactory == null)
                    baseContext.LoggerFactory = provider.GetService<ILoggerFactory>();

                return new GrowthBookFactory(baseContext);
            });

            services.AddScoped<IGrowthBook>(provider =>
            {
                var factory = provider.GetRequiredService<GrowthBookFactory>();
                return factory.CreateForUser(new System.Collections.Generic.Dictionary<string, object>());
            });

            return services;
        }
    }
}
