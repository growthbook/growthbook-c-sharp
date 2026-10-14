using System;

namespace GrowthBook
{
    /// <summary>
    /// Raises <c>FeaturesRefreshed</c> so that one subscriber cannot stop the others.
    /// </summary>
    /// <remarks>
    /// The event travels worker to repository to instance, and a repository is shared by every instance
    /// built on it. Invoking the delegate as a whole lets the first subscriber to throw stop every
    /// subscriber after it - on a shared repository that means one instance's handler silences the
    /// refresh for all the others, and on the SSE path it ends the read loop. Each subscriber is called
    /// on its own and its exception is logged instead.
    /// </remarks>
    internal static class FeaturesRefreshedNotifier
    {
        public static void Raise(
            EventHandler<FeaturesRefreshedEventArgs> handler,
            object sender,
            FeaturesRefreshedEventArgs args,
            Action<Exception> onSubscriberFailure)
        {
            if (handler is null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<FeaturesRefreshedEventArgs>)subscriber)(sender, args);
                }
                catch (Exception ex)
                {
                    onSubscriberFailure(ex);
                }
            }
        }
    }
}
