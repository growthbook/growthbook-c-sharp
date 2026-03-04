using System.Text.Json.Serialization;
using GrowthBook.Tests.StandardTests.UtilitiesTests;

namespace GrowthBook.Tests.Json;

[JsonSerializable(typeof(GetBucketRangeTests.BucketRangeConfiguration))]
[JsonSerializable(typeof(GetBucketRangeTests.BucketRangeTestCase))]
internal partial class TestJsonContext : JsonSerializerContext
{
}
