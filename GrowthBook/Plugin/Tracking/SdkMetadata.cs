using System.Reflection;

namespace GrowthBook.Plugin
{
    internal static class SdkMetadata
    {
        public const string Name = "growthbook-csharp-sdk";
        public const string Language = "csharp";
        public static readonly string Version = PackageVersion(typeof(SdkMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

        public static readonly string UserAgent = Name + "/" + Version;

        /// <summary>
        /// The package version alone. Builds with source link append the commit (<c>2.0.0+abc123</c>), which would
        /// split every release into one version per commit wherever it is reported.
        /// </summary>
        internal static string PackageVersion(string informationalVersion)
        {
            if (string.IsNullOrEmpty(informationalVersion))
            {
                return "unknown";
            }

            var metadata = informationalVersion.IndexOf('+');

            return metadata < 0 ? informationalVersion : informationalVersion.Substring(0, metadata);
        }
    }
}
