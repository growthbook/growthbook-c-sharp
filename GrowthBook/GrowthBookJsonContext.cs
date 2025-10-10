// GrowthBookJsonContext.cs
using System.Text.Json.Serialization;
using System.Collections.Generic;
using GrowthBook.Converters;
using GrowthBook;
using System.Text.Json.Nodes; // Припускаємо, що Feature знаходиться тут

namespace GrowthBook
{
    // Клас FeaturesResponse, який був вкладеним, 
    // має бути тут (або в окремому файлі, щоб SG міг його бачити)
    // Якщо він залишиться private sealed class всередині іншого класу, SG його не побачить.

    // В ідеалі, винесіть FeaturesResponse в окремий файл/клас
    // і зробіть його internal/public.

    // Це клас для Source Generator, який генерує необхідний код

    // ...

    [JsonSerializable(typeof(FeaturesResponse))]
    [JsonSerializable(typeof(Dictionary<string, Feature>))]
    [JsonSerializable(typeof(IDictionary<string, object>))]
    [JsonSerializable(typeof(Context))]
    [JsonSerializable(typeof(ExperimentAssignment))]
    [JsonSerializable(typeof(Feature))]
    [JsonSerializable(typeof(FeatureResult))]

    // ===================================
    // ДОДАНО/ОНОВЛЕНО
    // ===================================
    [JsonSerializable(typeof(FeatureRule))]
    [JsonSerializable(typeof(Experiment))]
    [JsonSerializable(typeof(ExperimentResult))]

    // Namespace & BucketRange (самі DTO)
    [JsonSerializable(typeof(Namespace))]
    [JsonSerializable(typeof(BucketRange))]

    // КОНТЕЙНЕРИ: Списки та Словники (MUST HAVE!)
    [JsonSerializable(typeof(IList<ParentCondition>))]
    [JsonSerializable(typeof(IList<BucketRange>))]
    [JsonSerializable(typeof(IList<VariationMeta>))]
    [JsonSerializable(typeof(IList<Filter>))]
    [JsonSerializable(typeof(IList<UrlPattern>))] // Якщо цей клас використовується
    [JsonSerializable(typeof(IDictionary<string, StickyAssignmentsDocument>))]
    [JsonSerializable(typeof(RemoteEvaluationRequest))]
    [JsonSerializable(typeof(RemoteEvaluationResponse))] // <-- ДОДАЙТЕ ЦЕЙ КЛАС
    [JsonSerializable(typeof(Dictionary<string, Feature>))]
    [JsonSerializable(typeof(Dictionary<string, string?>))]
    [JsonSerializable(typeof(JsonArray))]
    [JsonSerializable(typeof(object[]))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(int[]))]
    [JsonSerializable(typeof(object))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(Dictionary<string, int>))]
    // Якщо цей клас використовується

    // ... інші DTO (ParentCondition, VariationMeta, Filter, UrlPattern, StickyAssignmentsDocument)

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        GenerationMode = JsonSourceGenerationMode.Default,
        WriteIndented = false)]

    internal partial class GrowthBookJsonContext : JsonSerializerContext
    {
    }
}
