using System;
using System.Collections.Generic;

namespace GrowthBook
{
    /// <summary>
    /// Extension methods for working with Experiment custom fields.
    /// </summary>
    public static class ExperimentExtensions
    {
        /// <summary>
        /// Gets a custom field value by its ID.
        /// </summary>
        /// <param name="experiment">The experiment instance.</param>
        /// <param name="fieldId">The custom field ID (e.g., "cfl_4bzy5k3zmcjet8q5").</param>
        /// <returns>The custom field value, or null if not found.</returns>
        public static object GetCustomField(this Experiment experiment, string fieldId)
        {
            if (experiment?.CustomFields == null) return null;
            return experiment.CustomFields.TryGetValue(fieldId, out var value) ? value : null;
        }

        /// <summary>
        /// Gets a custom field value cast to the specified type.
        /// </summary>
        /// <typeparam name="T">The type to cast to.</typeparam>
        /// <param name="experiment">The experiment instance.</param>
        /// <param name="fieldId">The custom field ID.</param>
        /// <returns>The custom field value cast to type T, or default(T) if not found or cast fails.</returns>
        public static T GetCustomField<T>(this Experiment experiment, string fieldId)
        {
            var value = experiment.GetCustomField(fieldId);
            if (value == null) return default(T);

            try
            {
                if (value is T typedValue) return typedValue;
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default(T);
            }
        }

        /// <summary>
        /// Checks if a custom field exists.
        /// </summary>
        /// <param name="experiment">The experiment instance.</param>
        /// <param name="fieldId">The custom field ID.</param>
        /// <returns>True if the field exists, false otherwise.</returns>
        public static bool HasCustomField(this Experiment experiment, string fieldId)
        {
            return experiment?.CustomFields?.ContainsKey(fieldId) ?? false;
        }
    }
}
