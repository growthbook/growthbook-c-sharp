using System.Reflection;

namespace GrowthBook.Plugin
{
    internal static class SdkMetadata
    {
        public const string Name = "growthbook-csharp-sdk";
        public const string Language = "csharp";
        public static readonly string Version = typeof(SdkMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        public static readonly string UserAgent = Name + "/" + Version;
    }
}
