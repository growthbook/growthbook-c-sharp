using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using GrowthBook.Tests.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests;

/// <summary>
/// Represents a unit test and provides basic functionality for executing JSON-based test cases.
/// </summary>
public abstract class UnitTest
{
    /// <summary>
    /// Represents a named test category within the custom-cases.json tests.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    protected sealed class CustomCaseTestCategoryAttribute : TestCategoryAttribute
    {
        public CustomCaseTestCategoryAttribute(string name)
            : base("custom-cases", name)
        {

        }
    }

    /// <summary>
    /// Represents a named test category within the standard-cases.json tests.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    protected sealed class StandardCaseTestCategoryAttribute : TestCategoryAttribute
    {
        public StandardCaseTestCategoryAttribute(string name)
            : base("standard-cases", name)
        {

        }
    }

    protected abstract class TestCategoryAttribute : Attribute
    {
        /// <summary>
        /// Gets the name of the embedded JSON resource file without file extension.
        /// </summary>
        public string Resource { get; }

        /// <summary>
        /// Gets the name of the test category within the resource.
        /// </summary>
        public string Name { get; }

        public TestCategoryAttribute(string resource, string name)
        {
            Resource = resource;
            Name = name;
        }
    }

    /// <summary>
    /// Represents a class property that should be populated based on
    /// an index in a JSON array. This mapping provides an easy way to move
    /// from the unnamed parts of the JSON arrays in the standard test cases.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    protected sealed class TestPropertyIndexAttribute : Attribute
    {
        /// <summary>
        /// The zero-based index of the JSON array that this property is populated from.
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// Gets whether this value might be omitted in the test JSON.
        /// </summary>
        public bool IsOptional { get; }

        public TestPropertyIndexAttribute(int index) => Index = index;
        public TestPropertyIndexAttribute(int index, bool isOptional) : this(index) => IsOptional = isOptional;
    }

    /// <summary>
    /// Retrieves all test cases by category name from the associated embedded resource.
    /// </summary>
    /// <param name="categoryType">The type representing a test case within a category.</param>
    /// <returns>
    /// A sequence of object arrays. Each array contains all test data that will be passed to
    /// the test method for a single test case. All of the category-based tests will take a single
    /// parameter that is the type of the test case class.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when unable to locate the <see cref="GetTestsInCategory{T}"/> method.</exception>
    public static IEnumerable<object[]> GetMappedTestsInCategory(Type categoryType)
    {
        var getTests = typeof(UnitTest).GetMethod(nameof(GetTestsInCategory), BindingFlags.Static | BindingFlags.NonPublic);

        if (getTests is null)
        {
            throw new InvalidOperationException($"Attempted to get test retrieval method '{nameof(GetTestsInCategory)}' but could not find a suitable match");
        }

        var concreteMethod = getTests.MakeGenericMethod(categoryType);

        var testsInCategory = (IEnumerable<object>)concreteMethod.Invoke(null, null);

        return testsInCategory.Select(x => new object[] { x });
    }

    /// <summary>
    /// Retrieves all test cases within a single test category. 
    /// </summary>
    /// <typeparam name="T">The type of the test case class.</typeparam>
    /// <returns>A sequence of test case instances.</returns>
    protected static IEnumerable<T> GetTestsInCategory<T>() where T : new()
    {
        var category = typeof(T).GetCustomAttribute<TestCategoryAttribute>();

        if (category is null)
        {
            return Enumerable.Empty<T>();
        }

        return GetTestJsonCategoryAs<T>(category.Resource, category.Name);
    }

    /// <summary>
    /// Retrieves all test cases within a single test category by resource name and category name.
    /// </summary>
    /// <typeparam name="T">The type of the test case class.</typeparam>
    /// <param name="resourceName">The name of the embedded JSON resource without file extension.</param>
    /// <param name="testCategory">The name of the test case category.</param>
    /// <returns>A sequence of test case instances.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the embedded resource or test category could not be located.</exception>
    private static IEnumerable<T> GetTestJsonCategoryAs<T>(string resourceName, string testCategory) where T : new()
    {
        var qualifiedPath = $"GrowthBook.Tests.Json.{resourceName}.json";

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(qualifiedPath);

        if (stream == null)
        {
            throw new InvalidOperationException($"The resource {resourceName} is not available - make sure that it has Build Action set to Embedded Resource");
        }

        using StreamReader reader = new StreamReader(stream);

        var json = reader.ReadToEnd();
        var jsonObject = JObject.Parse(json);
        var tests = (JArray)jsonObject.SelectToken(testCategory);

        if (tests is null)
        {
            throw new InvalidOperationException($"The resource '{resourceName}' does not contain a test category named '{testCategory}'");
        }

        return tests.Select(x => DeserializeFromJsonArray<T>((JArray)x));
    }

    private static T DeserializeFromJsonArray<T>(JArray array) where T : new()
    {
        return (T)DeserializeFromJsonArray(array, typeof(T));
    }

    private static object? DeserializeFromJsonArray(JArray array, Type instanceType)
    { 
        if (array is null)
        {
            return default;
        }

        // Transforming a JSON array into a .Net array requires us to copy the elements over,
        // so handle that case up front before we try anything else.

        if (instanceType.IsArray)
        {
            var arrayElements = Array.CreateInstance(instanceType.GetElementType(), array.Count);

            for(var i = 0; i < array.Count; i++)
            {
                arrayElements.SetValue(array[i].ToObject(instanceType.GetElementType()), i);
            }

            return arrayElements;
        }

        // If there isn't a public parameterless constructor available then it's likely that this is a
        // tuple value that depends on its associated JsonConverter to be created correctly. Fall back to
        // the JSON conversion entirely for this type.

        if (instanceType.GetConstructor(Type.EmptyTypes) is null)
        {
            return array.ToObject(instanceType);
        }

        // This isn't an array or a tuple so we need to assume it's a common type with properties and proceed accordingly.

        var instance = Activator.CreateInstance(instanceType);

        foreach(var property in instanceType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var indexAttribute = property.GetCustomAttribute<TestPropertyIndexAttribute>();

            var testIndex = indexAttribute?.Index;
            var isOptional = indexAttribute?.IsOptional;

            if (testIndex is null)
            {
                continue;
            }

            if (testIndex < 0 || testIndex > array.Count)
            {
                throw new InvalidOperationException($"Unable to deserialize type '{instanceType}', property '{property.Name}' has an index of '{testIndex}' that is out of range");
            }

            if (testIndex == array.Count && isOptional == true)
            {
                // Some of the JSON tests may omit the last property, in which case
                // we should just fail gracefully and keep going here.

                continue;
            }

            var jsonInstance = array[testIndex];

            if (jsonInstance.Type == JTokenType.Array)
            {
                var propertyInstance = DeserializeFromJsonArray((JArray)jsonInstance, property.PropertyType);

                property.SetValue(instance, propertyInstance, null);
            }
            else
            {
                var propertyInstance = jsonInstance.ToObject(property.PropertyType);

                property.SetValue(instance, propertyInstance, null);
            }
        }

        return instance;
    }
}
