using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using GrowthBook.Extensions;
using GrowthBook.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        public static int? GetQueryStringOverride(string? id, string? url, int? numVariations)
        {
            if (url == null || url.IsNullOrWhitespace())
            {
                return null;
            }

            var res = new Uri(url);

            if (res.Query.IsNullOrWhitespace())
            {
                return null;
            }

            NameValueCollection qs = HttpUtility.ParseQueryString(res.Query);
            var variation = qs.Get(id);

            if (variation.IsNullOrWhitespace())
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
        public static bool InNamespace(string? userId, Namespace nSpace)
        {
            var n = HashUtilities.Hash("__" + nSpace.Id, userId, 1);
            return n >= nSpace.Start && n < nSpace.End;
        }

        /// <summary>
        /// Returns an array of floats with numVariations items that are all equal and sum to 1.
        /// </summary>
        /// <param name="numVariations">The number of variations to generate weights for.</param>
        /// <returns>Array of floats with numVariations items that are all equal and sum to 1.</returns>
        public static IEnumerable<double> GetEqualWeights(int numVariations)
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
        public static IEnumerable<BucketRange> GetBucketRanges(int numVariations, double coverage = 1f, IEnumerable<double>? weights = null)
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

            double totalWeight = allWeights.Sum();

            if (totalWeight < 0.99 || totalWeight > 1.01f)
            {
                allWeights = GetEqualWeights(numVariations).ToArray();
            }

            var cumulative = 0d;

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
        public static int ChooseVariation(double n, IList<BucketRange> ranges)
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
        public static bool InRange(double? number, BucketRange range) => number >= range.Start && number < range.End;

        public static bool IsUrlTargeted(string url, IEnumerable<UrlPattern> urlPatterns)
        {
            var allPatterns = urlPatterns.ToArray();

            if (!allPatterns.Any())
            {
                return false;
            }

            var hasIncludeRules = false;
            var isIncluded = false;

            foreach (var pattern in allPatterns)
            {
                var isMatch = EvaluateUrlTarget(url, pattern);

                if (!pattern.Include)
                {
                    if (isMatch)
                    {
                        return false;
                    }
                }
                else
                {
                    hasIncludeRules = true;

                    if (isMatch)
                    {
                        return isIncluded;
                    }
                }
            }

            return isIncluded || !hasIncludeRules;
        }

        private static bool EvaluateUrlTarget(string url, UrlPattern pattern)
        {
            var parsed = new Uri(url);

            if (pattern.Type == "regex")
            {
                var regex = GetUrlRegex(pattern);

                if (regex is null)
                {
                    return false;
                }

                return
                    regex.IsMatch(parsed.AbsolutePath) ||
                    regex.IsMatch(parsed.AbsolutePath.Substring(parsed.Host.Length));
            }
            else if (pattern.Type == "simple")
            {
                return EvaluateSimpleUrlTarget(parsed, pattern);
            }

            return false;
        }

        private static bool EvaluateSimpleUrlTarget(Uri actual, UrlPattern pattern)
        {
            // If a protocol is missing, but a host is specified, add `https://` to the front
            // Use "_____" as the wildcard since `*` is not a valid hostname in some browsers

            var currentPattern = pattern.Pattern ?? "";

            var match = Regex.Match(currentPattern, "^([^:/?]*)\\.");

            if (match.Success)
            {
                currentPattern = $"https://{currentPattern}";
            }

            var expected = currentPattern.Replace("*", "_____");
            var expectedUri = new Uri($"https://{expected}");

            // Compare each part of the URL separately

            var comparisons = new List<(string Actual, string Expected, bool IsPath)>
            {
                (actual.Host, expectedUri.Host, false),
                (actual.AbsolutePath, expectedUri.AbsolutePath, true)
            };

            // We only want to compare hashes if it's explicitly being targeted

            if (expectedUri.ContainsHashInPath())
            {
                comparisons.Add((actual.GetHashContents() ?? string.Empty, expectedUri.GetHashContents() ?? string.Empty, false));
            }

            var actualQueryParameters = HttpUtility.ParseQueryString(actual.Query);
            var expectedQueryParameters = HttpUtility.ParseQueryString(expectedUri.Query);

            for(var i = 0; i < expectedQueryParameters.Count; i++)
            {
                comparisons.Add((actualQueryParameters[i] ?? string.Empty, expectedQueryParameters[i] ?? string.Empty, false));
            }

            // Any failure means the whole thing fails.

            return comparisons.Any(x => !EvaluateSimpleUrlPart(x.Actual, x.Expected, x.IsPath));
        }

        private static bool EvaluateSimpleUrlPart(string actual, string expected, bool isPath)
        {
            var escaped = Regex.Replace(expected, @"[*.+?^${}()|[\]\\]", @"\$&");
            var escapedWithWildcards = escaped.Replace("_____", ".*");

            if (isPath)
            {
                // When matching path name, make leading/trailing slashes optional

                escapedWithWildcards = Regex.Replace(escapedWithWildcards, @"(^\/|\/$)", string.Empty);
                escapedWithWildcards = $@"\/?{escapedWithWildcards}\/?";
            }

            var regex = new Regex($"^{escapedWithWildcards}$");

            return regex.IsMatch(actual);
        }

        private static Regex? GetUrlRegex(UrlPattern pattern)
        {
            try
            {
                var match = Regex.IsMatch(pattern.Pattern ?? "", @"([^\\])\/");
                var escaped = Regex.Replace(pattern.Pattern ?? "", @"([^\\])\/", @"$1\/");

                return new Regex(escaped);
            }
            catch
            {
                return default;
            }
        }

        public static (StickyAssignmentsDocument Document, bool IsChanged) GenerateStickyBucketAssignment(IStickyBucketService stickyBucketService, string? attributeName, string? attributeValue, IDictionary<string, string?> assignments)
        {
            var existingDocument = stickyBucketService is null ? new StickyAssignmentsDocument(attributeName, attributeValue) : stickyBucketService.GetAssignments(attributeName, attributeValue);
            var newAssignments = new Dictionary<string, string?>(existingDocument?.Assignments ?? new Dictionary<string, string?>());

            newAssignments.MergeWith(new[] { assignments });

            var isChanged = JsonConvert.SerializeObject(existingDocument) != JsonConvert.SerializeObject(newAssignments);
            var document = new StickyAssignmentsDocument(attributeName, attributeValue, newAssignments);

            return (document, isChanged);
        }

        public static StickyBucketVariation GetStickyBucketVariation(Experiment experiment, int bucketVersion, int minBucketVersion, IList<VariationMeta> meta, JObject? attributes, IDictionary<string, StickyAssignmentsDocument> document)
        {
            var id = GetStickyBucketExperimentKey(experiment.Key, experiment.BucketVersion);
            var assignments = GetStickyBucketAssignments(attributes, document, experiment.HashAttribute, experiment.FallbackAttribute);

            if (experiment.MinBucketVersion > 0)
            {
                for(var i = 0; i <= experiment.MinBucketVersion; i++)
                {
                    var blockedKey = GetStickyBucketExperimentKey(experiment.Key, i);

                    if (assignments.ContainsKey(blockedKey))
                    {
                        return new StickyBucketVariation(-1, isVersionBlocked: true);
                    }
                }
            }

            if (!assignments.TryGetValue(id, out var variationKey))
            {
                return new StickyBucketVariation(-1, isVersionBlocked: false);
            }

            var variationIndex = FindVariationIndex(meta, variationKey);
                        
            return new StickyBucketVariation(variationIndex, isVersionBlocked: false);
        }

        private static IDictionary<string, string?> GetStickyBucketAssignments(JObject? attributes, IDictionary<string, StickyAssignmentsDocument> stickyAssignmentDocs, string hashAttribute, string? fallbackAttribute)
        {
            var mergedAssignments = new Dictionary<string, string?>();

            if (stickyAssignmentDocs is null)
            {
                return mergedAssignments;
            }

            (var hashAttributeWithoutFallback, var hashValueWithoutFallback) = attributes.GetHashAttributeAndValue(hashAttribute, default);
            var hashKey = new StickyAssignmentsDocument(hashAttributeWithoutFallback, hashValueWithoutFallback);

            (var hashAttributeWithFallback, var hashValueWithFallback) = attributes.GetHashAttributeAndValue(fallbackAttribute, default);
            var fallbackKey = new StickyAssignmentsDocument(hashAttributeWithFallback, hashValueWithFallback);

            var pendingAssignments = new List<IDictionary<string, string?>>();

            // We're grabbing any fallback values first so that the original can override them if present as well.

            if (fallbackKey.HasValue && stickyAssignmentDocs.TryGetValue(fallbackKey.FormattedAttribute, out var fallbackDocument))
            {
                pendingAssignments.Add(fallbackDocument.Assignments);
            }

            if (stickyAssignmentDocs.TryGetValue(hashKey.FormattedAttribute, out var document))
            {
                pendingAssignments.Add(document.Assignments);
            }

            return mergedAssignments.MergeWith(pendingAssignments);
        }

        private static int FindVariationIndex(IList<VariationMeta> meta, string? key)
        {
            for(var i = 0; i < meta.Count; i++)
            {
                if (meta[i].Key == key)
                {
                    return i;
                }
            }

            return -1;
        }

        public static string GetStickyBucketExperimentKey(string? key, int bucketVersion) => $"{key}__{bucketVersion}";
    }
}
