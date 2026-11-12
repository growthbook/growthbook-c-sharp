using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Utilities
{
    /// <summary>
    /// Deep copies of loaded state, for the snapshots the SDK hands out.
    /// </summary>
    internal static class StateCopy
    {
        /// <summary>
        /// Settings of the SDK's own rather than <see cref="JsonSerializer.CreateDefault()"/>, which folds in whatever the
        /// host put in <see cref="JsonConvert.DefaultSettings"/>: under <c>DefaultValueHandling.Ignore</c> a disabled
        /// experiment would lose <c>Active = false</c> and come back enabled.
        /// </summary>
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.Include,
            NullValueHandling = NullValueHandling.Include,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            TypeNameHandling = TypeNameHandling.None
        });

        internal static T Of<T>(T value) where T : class
        {
            if (value is null)
            {
                return null;
            }

            return JToken.FromObject(value, Serializer).ToObject<T>(Serializer);
        }
    }
}
