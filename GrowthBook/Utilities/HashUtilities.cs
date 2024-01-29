using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Utilities
{
    internal static class HashUtilities
    {
        /// <summary>
        /// Hashes a string to a float between 0 and 1 using the simple Fowler–Noll–Vo algorithm (fnv32a).
        /// </summary>
        /// <param name="value">The string to hash.</param>
        /// <returns>float between 0 and 1, null if an unsupported version.</returns>
        public static double? Hash(string seed, string value, int version)
        {
            // New hashing algorithm.

            if (version == 2)
            {
                var n = FNV32A(FNV32A(seed + value).ToString());
                return (n % 10000) / 10000d;
            }

            // Original hashing algorithm (with a bias flaw).

            if (version == 1)
            {
                var n = FNV32A(value + seed);
                return (n % 1000) / 1000d;
            }

            return null;
        }

        /// <summary>
        /// Implementation of the Fowler–Noll–Vo algorithm (fnv32a) algorithm.
        /// </summary>
        /// <param name="value">The value to hash.</param>
        /// <returns>The hashed value.</returns>
        private static uint FNV32A(string value)
        {
            uint hash = 0x811c9dc5;
            uint prime = 0x01000193;

            foreach (char c in value.ToCharArray())
            {
                hash ^= c;
                hash *= prime;
            }

            return hash;
        }
    }
}
