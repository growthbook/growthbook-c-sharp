using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Tests that set <c>JsonConvert.DefaultSettings</c>, which is global. Parallelism is disabled for the
/// collection so that a test changing it cannot be observed by one running at the same moment.
/// </summary>
[CollectionDefinition("SerializerSettings", DisableParallelization = true)]
public class SerializerSettingsCollection
{
}
