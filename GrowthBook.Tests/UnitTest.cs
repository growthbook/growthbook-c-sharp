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

public abstract class UnitTest
{
    protected sealed class CustomCaseTestCategoryAttribute : TestCategoryAttribute
    {
        public CustomCaseTestCategoryAttribute(string name)
            : base("custom-cases", name)
        {

        }
    }

    protected sealed class StandardCaseTestCategoryAttribute : TestCategoryAttribute
    {
        public StandardCaseTestCategoryAttribute(string name)
            : base("standard-cases", name)
        {

        }
    }

    protected abstract class TestCategoryAttribute : Attribute
    {
        public string Resource { get; }
        public string Name { get; }

        public TestCategoryAttribute(string resource, string name)
        {
            Resource = resource;
            Name = name;
        }
    }

    protected sealed class TestPropertyIndexAttribute : Attribute
    {
        public int Index { get; }

        public TestPropertyIndexAttribute(int index) => Index = index;
    }

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
    /// Returns the contents of the embedded resource as a string.
    /// </summary>
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
        var tests = (JArray)jsonObject[testCategory];

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

        // Transforming an array into an array requires us to copy the elements over,
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
            var testIndex = property.GetCustomAttribute<TestPropertyIndexAttribute>()?.Index;

            if (testIndex is null)
            {
                continue;
            }

            if (testIndex < 0 || testIndex > array.Count)
            {
                throw new InvalidOperationException($"Unable to deserialize type '{instanceType}', property '{property.Name}' has an index of '{testIndex}' that is out of range");
            }

            var jsonInstance = array[testIndex];

            if (jsonInstance.Type == JTokenType.Array)
            {
                var deserializeArray = typeof(UnitTest).GetMethod(nameof(DeserializeFromJsonArray), BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(JArray), typeof(Type) });
                var propertyInstance = deserializeArray.Invoke(null, new object[] { jsonInstance, property.PropertyType });

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
