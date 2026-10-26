using System;
using System.Threading.Tasks;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        /// call rather than held. Features are loaded once when the container first resolves it, so the
        /// first evaluation does not race the first load.
        /// </remarks>
        public static IServiceCollection AddGrowthBookClient(this IServiceCollection services, Action<Options> configureOptions)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (configureOptions is null) throw new ArgumentNullException(nameof(configureOptions));

            return services.AddSingleton(provider =>
            {
                var options = new Options();

                configureOptions(options);

                return CreateInitializedClient(options, provider);
            });
        }

        /// <summary>
        /// Registers a <see cref="GrowthBookClient"/> singleton from a prepared <see cref="Options"/>.
        /// </summary>
        public static IServiceCollection AddGrowthBookClient(this IServiceCollection services, Options options)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (options is null) throw new ArgumentNullException(nameof(options));

            return services.AddSingleton(provider => CreateInitializedClient(options, provider));
        }

        /// <summary>
        /// Builds the client and loads its features before handing it to the container.
        /// </summary>
        /// <remarks>
        /// The load is started on the thread pool rather than awaited in place. A container resolves a
        /// singleton on whichever thread asked for it first, and on a host with a single-threaded
        /// synchronization context - a desktop UI thread, classic ASP.NET - blocking there while the
        /// continuation waits for that same thread is a deadlock.
        /// </remarks>
        private static GrowthBookClient CreateInitializedClient(Options options, IServiceProvider provider)
        {
            if (options.LoggerFactory is null)
            {
                options.LoggerFactory = provider.GetService<ILoggerFactory>();
            }

            var client = new GrowthBookClient(options);

            Task.Run(() => client.InitializeAsync()).GetAwaiter().GetResult();

            return client;
        }
    }
}
