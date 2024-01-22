using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Web;
using GrowthBook.Extensions;

namespace GrowthBook.Utilities
{
    internal static class ExperimentUtilities
    {
        /// <summary>
        /// Checks if an experiment variation is being forced via a URL query string.
        /// </summary>
        /// <param name="id">The id field to search for in the query string.</param>
        /// <param name="url">The url to search.</param>
        /// <param name="numVariations">The number of variations in the experiment.</param>
        /// <returns>The overridden variation id, or null if not found.</returns>
        public static int? GetQueryStringOverride(string id, string url, int numVariations)
        {
            if (url.IsMissing())
            {
                return null;
            }

            var res = new Uri(url);

            if (res.Query.IsMissing())
            {
                return null;
            }

            NameValueCollection qs = HttpUtility.ParseQueryString(res.Query);
            var variation = qs.Get(id);

            if (variation.IsMissing())
            {
                return null;
            }

            if (!int.TryParse(variation, out var varId))
            {
                return null;
            }

            if (varId < 0 || varId >= numVariations)
            {
                return null;
            }

            return varId;
        }

        /// <summary>
        /// Checks if a userId is within an experiment namespace or not.
        /// </summary>
        /// <param name="userId">The user id string to check.</param>
        /// <param name="nSpace">The namespace to check.</param>
        /// <returns>True if the userid is within the experiment namespace.</returns>
        public static bool InNamespace(string userId, Namespace nSpace)
        {
            var n = HashUtilities.Hash("__" + nSpace.Id, userId, 1);
            return n >= nSpace.Start && n < nSpace.End;
        }

        /// <summary>
        /// Returns an array of floats with numVariations items that are all equal and sum to 1.
        /// </summary>
        /// <param name="numVariations">The number of variations to generate weights for.</param>
        /// <returns>Array of floats with numVariations items that are all equal and sum to 1.</returns>
        public static IEnumerable<float> GetEqualWeights(int numVariations)
        {
            if (numVariations >= 1)
            {
                for (int i = 0; i < numVariations; i++)
                {
                    yield return (1.0f / numVariations);
                }
            }
        }

        /// <summary>
        /// Converts an experiment's coverage and variation weights into an list of bucket ranges.
        /// </summary>
        /// <param name="numVariations">The number of variations to convert.</param>
        /// <param name="coverage">The experiment's coverage (defaults to 1).</param>
        /// <param name="weights">Optional list of variant weights.</param>
        /// <returns>A list of bucket ranges.</returns>
        public static IEnumerable<BucketRange> GetBucketRanges(int numVariations, float coverage = 1f, IEnumerable<float> weights = null)
        {
            if (coverage < 0)
            {
                coverage = 0;
            }
            else if (coverage > 1)
            {
                coverage = 1;
            }

            var allWeights = weights?.ToArray();

            if (allWeights == null || allWeights.Length != numVariations)
            {
                allWeights = GetEqualWeights(numVariations).ToArray();
            }

            float totalWeight = allWeights.Sum();

            if (totalWeight < 0.99 || totalWeight > 1.01f)
            {
                allWeights = GetEqualWeights(numVariations).ToArray();
            }

            var cumulative = 0f;

            for (int i = 0; i < allWeights.Length; i++)
            {
                var start = cumulative;
                cumulative += allWeights[i];
                yield return new BucketRange(start, (start + coverage * allWeights[i]));
            }
        }

        /// <summary>
        /// Given a hash and bucket ranges, assign one of the bucket ranges.
        /// </summary>
        /// <param name="n">The hash value.</param>
        /// <param name="ranges">LIst of bucket ranges to compare the hash to.</param>
        /// <returns>The selected variation id, or -1 if no match is found.</returns>
        public static int ChooseVariation(float n, IList<BucketRange> ranges)
        {
            for (int i = 0; i < ranges.Count; i++)
            {
                if (InRange(n, ranges[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Determine whether the given number falls within the provided bucket range.
        /// </summary>
        /// <param name="number">The number to verify.</param>
        /// <param name="range">The bucket range.</param>
        /// <returns>True if the value is in the range, false otherwise.</returns>
        public static bool InRange(float number, BucketRange range) => number >= range.Start && number < range.End;
    }
}
