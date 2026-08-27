using System;
using System.Collections.Generic;

namespace GrowthBook.Api
{
    /// <summary>
    /// Holds the handlers registered against an <see cref="IFeatureRefreshSource"/> and fans a refresh out
    /// to all of them. Shared by the cache and the repository so both implement the contract the same way.
    /// </summary>
    internal sealed class FeatureRefreshSubscriptions
    {
        private readonly object _lock = new object();
        private readonly List<Action<IDictionary<string, Feature>>> _handlers = new List<Action<IDictionary<string, Feature>>>();

        /// <summary>
        /// Registers a handler and returns the handle that removes it again.
        /// </summary>
        public IDisposable Add(Action<IDictionary<string, Feature>> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (_lock)
            {
                _handlers.Add(handler);
            }

            return new Subscription(this, handler);
        }

        /// <summary>
        /// Calls every registered handler with the new features. A handler that throws is reported to
        /// <paramref name="onHandlerError"/> and the remaining handlers still run, so one misbehaving
        /// subscriber cannot stop the others from seeing the refresh.
        /// </summary>
        public void Notify(IDictionary<string, Feature> features, Action<Exception> onHandlerError)
        {
            Action<IDictionary<string, Feature>>[] handlers;

            lock (_lock)
            {
                if (_handlers.Count == 0)
                {
                    return;
                }

                handlers = _handlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(features);
                }
                catch (Exception ex)
                {
                    onHandlerError?.Invoke(ex);
                }
            }
        }

        private void Remove(Action<IDictionary<string, Feature>> handler)
        {
            lock (_lock)
            {
                _handlers.Remove(handler);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly FeatureRefreshSubscriptions _owner;
            private Action<IDictionary<string, Feature>> _handler;

            public Subscription(FeatureRefreshSubscriptions owner, Action<IDictionary<string, Feature>> handler)
            {
                _owner = owner;
                _handler = handler;
            }

            public void Dispose()
            {
                var handler = _handler;

                if (handler is null)
                {
                    return;
                }

                _handler = null;
                _owner.Remove(handler);
            }
        }
    }
}
