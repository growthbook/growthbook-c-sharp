using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GrowthBook.MultiUser.Configuration
{
    /// <summary>Combines global and user context for a single stateless evaluation pass.</summary>
    internal sealed class EvaluationContext
    {
        private JObject _attributes;

        public GlobalContext Global { get; }
        public UserContext User { get; }
        public StackContext Stack { get; }

        public EvaluationContext(GlobalContext global, UserContext user)
        {
            Global = global;
            User = user;
            Stack = new StackContext();
        }

        /// <summary>Merges global and user forced variations. User values take precedence.</summary>
        public IDictionary<string, int> GetForcedVariations()
        {
            var result = new Dictionary<string, int>();
            if (Global.ForcedVariations != null)
                foreach (var kv in Global.ForcedVariations) result[kv.Key] = kv.Value;
            if (User.ForcedVariations != null)
                foreach (var kv in User.ForcedVariations) result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>Merges global and user forced feature values. User values take precedence.</summary>
        public IDictionary<string, JToken> GetForcedFeatureValues()
        {
            var result = new Dictionary<string, JToken>();
            if (Global.ForcedFeatureValues != null)
                foreach (var kv in Global.ForcedFeatureValues) result[kv.Key] = kv.Value;
            if (User.ForcedFeatureValues != null)
                foreach (var kv in User.ForcedFeatureValues) result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>
        /// Merges global and user attributes. User attributes and overrides take precedence.
        /// </summary>
        /// <remarks>
        /// Merged once and kept for the life of this context, which is a single evaluation. Every rule asks
        /// for the attributes two or three times and the merge copies each value, so recomputing it per call
        /// put a full copy of the user's attributes on the heap for each rule of each feature evaluated. The
        /// returned object is shared by those callers and must be treated as read only.
        /// </remarks>
        public JObject GetAttributes()
        {
            if (_attributes != null)
                return _attributes;

            var result = Global.Attributes?.DeepClone() as JObject ?? new JObject();
            if (User.Attributes != null)
                foreach (var prop in User.Attributes.Properties())
                    result[prop.Name] = prop.Value; // user overrides global
            if (User.AttributeOverrides != null)
                foreach (var prop in User.AttributeOverrides.Properties())
                    result[prop.Name] = prop.Value;

            _attributes = result;

            return result;
        }
    }
}
