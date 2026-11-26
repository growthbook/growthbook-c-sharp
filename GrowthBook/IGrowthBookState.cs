using System;

namespace GrowthBook
{
    /// <summary>
    /// An <see cref="IGrowthBook"/> that can also be asked about its own lifetime.
    /// </summary>
    /// <remarks>
    /// A derived interface rather than members on <see cref="IGrowthBook"/>, which targets
    /// netstandard2.0 and has no default interface members: adding one there would stop every
    /// hand-written implementation compiling.
    /// </remarks>
    public interface IGrowthBookState : IGrowthBook
    {
        /// <summary>
        /// Whether this instance has been torn down. True from the moment teardown begins.
        /// </summary>
        bool IsDestroyed { get; }

        /// <summary>
        /// Registers a callback to run once on teardown, before the instance's state is cleared.
        /// Registering after teardown does nothing, matching the JavaScript SDK.
        /// </summary>
        /// <param name="callback">Null is ignored.</param>
        /// <returns>A handle that unregisters the callback, matching <see cref="IGrowthBook.Subscribe"/>.</returns>
        IDisposable OnDestroy(Action callback);
    }
}
