using System;
using System.IO;
using System.Reflection;

namespace GrowthBook.Tests.Json;

public static class JsonTestHelpers
{
    public static string GetTestJson(string jsonName)
    {
        return GetTestJson(
            Assembly.GetExecutingAssembly(),
            $"{typeof(JsonTestHelpers).Namespace}/{jsonName}.json");
    }

    /// <summary>
    ///     Returns the contents of the embedded resource as a string.
    /// </summary>
    public static string GetTestJson(Assembly assembly, string resourceName)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName.Replace('/', '.'));

        if (stream == null)
        {
            throw new InvalidOperationException($"The resource {resourceName} is not available - make sure that it has Build Action set to Embedded Resource");
        }

        using StreamReader reader = new StreamReader(stream);

        var json = reader.ReadToEnd();
        return json;
    }
}
