using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Extensions
{
    public static class UriExtensions
    {
        public static bool ContainsHashInPath(this Uri uri) => uri.AbsolutePath.Contains("#");

        public static string? GetHashContents(this Uri uri)
        {
            if (!uri.ContainsHashInPath())
            {
                return default;
            }

            var hashIndex = uri.AbsolutePath.IndexOf("#");

            return uri.AbsolutePath.Substring(hashIndex);
        }
    }
}
