using System;
using System.Threading.Tasks;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GrowthBook.Extensions
{
    /// <summary>
    /// Registers GrowthBook with a dependency injection container.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers a <see cref="GrowthBookClient"/> singleton configured by a delegate.
        /// </summary>
        /// <remarks>
        /// A singleton because one client serves every user in the process; the user is passed to each
        /// call rather than held. Features are loaded when the container first resolves it, so the
        /// first evaluation does not race the first load.
        /// </remarks>
        public static IServiceCollection AddGrowthBookClient(this IServiceCollection services, Action<Options> configureOptions)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (configureOptions is null) throw new ArgumentNullException(nameof(configureOptions));

            services.TryAddSingleton(provider =>
            {
                var options = new Options();

                configureOptions(options);

                return CreateInitializedClient(options, provider);
            });

            return services;
        }

        /// <summary>
        /// Registers a <see cref="GrowthBookClient"/> singleton from a prepared <see cref="Options"/>.
        /// </summary>
        /// <remarks>
        /// The options are copied, so registration does not write back into the instance the caller
        /// passed in and the client is not left sharing it.
        /// </remarks>
        public static IServiceCollection AddGrowthBookClient(this IServiceCollection services, Options options)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (options is null) throw new ArgumentNullException(nameof(options));

            var copy = options.Clone();

            services.TryAddSingleton(provider => CreateInitializedClient(copy, provider));

            return services;
        }

        /// <summary>
        /// Builds the client and loads its features before handing it to the container.
        /// </summary>
        /// <remarks>
        /// The load is started on the thread pool rather than awaited in place. A container resolves a
        /// singleton on whichever thread asked for it first, and on a host with a single-threaded
        /// synchronization context - a desktop UI thread, classic ASP.NET - blocking there while the
        /// continuation waits for that same thread is a deadlock.
        /// <para>
        /// A load that fails does not stop the client being created, because a feature API that is
        /// down at startup should not take the application down with it. Letting the exception escape
        /// would do exactly that, and worse: a container does not cache a singleton whose factory
        /// threw, so every later resolution would build another client, with its own repository,
        /// refresh worker and HTTP client, and block for another request timeout while doing it.
        /// </para>
        /// <para>
        /// The client then serves default values, and nothing brings it back on its own: streaming
        /// only starts once a load has succeeded, and no polling runs in between. The failure is
        /// logged as an error for that reason, and an application that cannot usefully run on default
        /// values should either call <see cref="GrowthBookClient.RefreshFeaturesAsync"/> itself or set
        /// <see cref="Options.ThrowOnInitializationFailure"/>.
        /// </para>
        /// </remarks>
        private static GrowthBookClient CreateInitializedClient(Options options, IServiceProvider provider)
        {
            if (options.LoggerFactory is null)
            {
                options.LoggerFactory = provider.GetService<ILoggerFactory>();
            }

            var client = new GrowthBookClient(options);

            try
            {
                Task.Run(() => client.InitializeAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (options.ThrowOnInitializationFailure)
                {
                    client.Dispose();

                    throw;
                }

                options.LoggerFactory?.CreateLogger(typeof(ServiceCollectionExtensions))
                    .LogError(ex, "GrowthBook could not load its features at startup. Every feature will evaluate to its default value until a refresh succeeds, and none is scheduled - call RefreshFeaturesAsync, or set Options.ThrowOnInitializationFailure to fail startup instead.");
            }

            return client;
        }
    }
}
