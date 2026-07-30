using System;

namespace GrowthBook.Api
{
    /// <summary>
    /// Controls how a failed remote evaluation request is retried.
    /// </summary>
    /// <remarks>
    /// Only failures that another attempt could plausibly fix are retried: transport errors, request timeouts and the
    /// server-side statuses 408, 429 and 5xx. A 4xx other than those means the request itself is wrong - a malformed
    /// payload or a bad client key - and repeating it would just fail the same way.
    /// </remarks>
    public class RemoteEvaluationRetryPolicy
    {
        private static readonly Random _jitter = new Random();
        private static readonly object _jitterLock = new object();

        /// <summary>
        /// Total number of attempts, the first one included. One means no retries. Defaults to 3.
        /// </summary>
        public int MaxAttempts { get; set; } = 3;

        /// <summary>
        /// How long to wait before the second attempt. Each further attempt doubles it, up to <see cref="MaxDelay"/>.
        /// Defaults to 500ms.
        /// </summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Upper bound on any single wait, including one asked for by a <c>Retry-After</c> header. Defaults to 5s.
        /// </summary>
        /// <remarks>
        /// Callers can be waiting on the evaluation (<c>UpdateAttributesAsync</c>, <c>LoadFeatures</c>), so an
        /// unbounded wait would stall them. Setting this to zero disables waiting altogether.
        /// </remarks>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How much to randomly spread each wait, as a fraction of its length. Defaults to 0.2, so a 500ms wait lands
        /// somewhere in 400-600ms.
        /// </summary>
        /// <remarks>
        /// Keeps many instances that failed on the same server outage from retrying in lockstep.
        /// </remarks>
        public double JitterRatio { get; set; } = 0.2;

        /// <summary>
        /// Calculates how long to wait before the next attempt.
        /// </summary>
        /// <param name="completedAttempts">How many attempts have already failed.</param>
        /// <param name="retryAfter">The delay the server asked for, if it sent one.</param>
        /// <returns>The time to wait, never longer than <see cref="MaxDelay"/>.</returns>
        public TimeSpan GetRetryDelay(int completedAttempts, TimeSpan? retryAfter = null)
        {
            if (MaxDelay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            // A server that says when to come back knows better than the backoff curve does, but it doesn't get to
            // park the caller for an arbitrary length of time either.
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
            {
                return retryAfter.Value < MaxDelay ? retryAfter.Value : MaxDelay;
            }

            if (InitialDelay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var exponent = Math.Max(0, completedAttempts - 1);
            var milliseconds = InitialDelay.TotalMilliseconds * Math.Pow(2, exponent);

            milliseconds = Math.Min(milliseconds, MaxDelay.TotalMilliseconds);
            milliseconds *= GetJitterMultiplier();

            return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDelay.TotalMilliseconds));
        }

        private double GetJitterMultiplier()
        {
            if (JitterRatio <= 0)
            {
                return 1d;
            }

            var ratio = Math.Min(JitterRatio, 1d);

            // Random isn't thread safe, and several instances can be backing off at once.
            lock (_jitterLock)
            {
                return 1d - ratio + (_jitter.NextDouble() * ratio * 2d);
            }
        }
    }
}
