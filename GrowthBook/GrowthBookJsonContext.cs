using System.Text.Json.Serialization;
using System.Collections.Generic;
using GrowthBook.Converters;
using GrowthBook;
using System.Text.Json.Nodes;
using GrowthBook.Utilities;

namespace GrowthBook
{
    [JsonSerializable(typeof(FeaturesResponse))]
    [JsonSerializable(typeof(Dictionary<string, Feature>))]
    [JsonSerializable(typeof(IDictionary<string, object>))]
    [JsonSerializable(typeof(Context))]
    [JsonSerializable(typeof(ExperimentAssignment))]
    [JsonSerializable(typeof(Feature))]
    [JsonSerializable(typeof(FeatureResult))]
    [JsonSerializable(typeof(FeatureRule))]
    [JsonSerializable(typeof(Experiment))]
    [JsonSerializable(typeof(ExperimentResult))]
    [JsonSerializable(typeof(TrackData))]
    [JsonSerializable(typeof(CacheKeyData))]
    [JsonSerializable(typeof(Namespace))]
    [JsonSerializable(typeof(BucketRange))]
    [JsonSerializable(typeof(StickyAssignmentsDocument))]
    [JsonSerializable(typeof(IList<ParentCondition>))]
    [JsonSerializable(typeof(IList<BucketRange>))]
    [JsonSerializable(typeof(IList<VariationMeta>))]
    [JsonSerializable(typeof(IList<Filter>))]
    [JsonSerializable(typeof(IList<UrlPattern>))]
    [JsonSerializable(typeof(StickyAssignmentsDocument[]))]
    [JsonSerializable(typeof(Dictionary<string, StickyAssignmentsDocument>))]
    [JsonSerializable(typeof(RemoteEvaluationRequest))]
    [JsonSerializable(typeof(RemoteEvaluationResponse))]
    [JsonSerializable(typeof(Dictionary<string, Feature>))]
    [JsonSerializable(typeof(Dictionary<string, string?>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(IDictionary<string, string>))]
    [JsonSerializable(typeof(IDictionary<string, string?>))]
    [JsonSerializable(typeof(JsonArray))]
    [JsonSerializable(typeof(object[]))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(int[]))]
    [JsonSerializable(typeof(object))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(Dictionary<string, int>))]
    [JsonSerializable(typeof(Dictionary<string, object[]>))]
    [JsonSerializable(typeof(System.Text.Json.JsonElement))]
    [JsonSerializable(typeof(double[]))]

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        GenerationMode = JsonSourceGenerationMode.Metadata,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    internal partial class GrowthBookJsonContext : JsonSerializerContext
    {
    }
}
