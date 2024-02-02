![growthbook banner with csharp logo](https://docs.growthbook.io/images/hero-csharp-sdk.png)

# growthbook-c-sharp

Powerful feature flagging and A/B testing for C# apps using [GrowthBook](https://www.growthbook.io/)

[![nuget badge for growthbook](https://img.shields.io/nuget/v/growthbook-c-sharp?style=flat-square)](https://www.nuget.org/packages/growthbook-c-sharp)

## Table of contents

<!-- toc -->

- [growthbook-c-sharp](#growthbook-c-sharp)
  - [Table of contents](#table-of-contents)
  - [Usage examples](#usage-examples)
    - [Basic](#basic)
    - [Loading feature definitions from an API](#loading-feature-definitions-from-an-api)
      - [Generic getters](#generic-getters)

<!-- tocstop -->

## Usage examples

This library is based on the [GrowthBook SDK specs](https://docs.growthbook.io/lib/build-your-own) and should be compatible with the usage examples in the [GrowthBook docs](https://docs.growthbook.io/). For a more complete set of examples involving usage within ASP.Net Core, take a look at the C# examples in the [GrowthBook examples repository](https://github.com/growthbook/examples/tree/main/csharp-example/GrowthBookCSharpExamples).

Down below you'll find two basic examples on how the package works.

### Basic

1. Install GrowthBook

    ```csharp
    dotnet add package growthbook-c-sharp
    ```

2. Declare features

    ```csharp

    var staticFeatures = new Dictionary<string, Feature>
        {
            {"firstFeature", new Feature{ DefaultValue = true}},
            {"secondFeature", new Feature{ DefaultValue = false}}
        };
    ```

3. Create a context and add the features

    ```csharp
    var context = new Context
    {
        Enabled = true,
        Url = "",
        Features = staticFeatures
    };
    ```

4. Create the `GrowthBook` object with the context

    ```csharp
    using GrowthBook;
    //...

    var GrowthBook = new GrowthBook.GrowthBook(context);
    ```

5. Check whether a feature is enabled

    ```csharp
    GrowthBook.IsOn("firstFeature") // true
    GrowthBook.IsOn("secondFeature") // false
    ```

### Loading feature definitions from an API

Because feature definitions are typically loaded from API calls or cache, [Json.NET](https://www.nuget.org/packages/Newtonsoft.Json/13.0.2-beta1)
objects are used to represent arbitrary document types such as Attributes, Conditions, and Feature values.

To load your features from the GrowthBook API use the following example:

1. Create a result model for the API response

    ```csharp
    public class FeaturesResult
    {
        public HttpStatusCode Status { get; set; }
        public IDictionary<string, Feature>? Features { get; set; }
        public DateTimeOffset? DateUpdated { get; set; }
    }
    ```

2. call the endpoint and deserialize result

    ```csharp
     var url = "YOUR_GROWTHBOOK_URL/api/features/YOUR_API_KEY";
    var response = await client.GetAsync(url);

    if (response.IsSuccessStatusCode)
    {
        var content = await response.Content.ReadAsStringAsync();
        var featuresResult = JsonConvert.DeserializeObject<FeaturesResult>(content);
    }
    ```

3. Construct a context and initialize GrowthBook

    ```csharp
    var GrowthBook = new GrowthBook.GrowthBook(
        new Context {
            Enabled = true,
            Url = "",
            Features = featuresResult.Features,
        }
    );
    ```

#### Generic getters

To make it easier to deal with Feature values, generic getter functions are provided for the following:

- Experiment:
  - `GetVariations<T>()`
- ExperimentResult:
  - `GetValue<T>()`
- Feature:
  - `GetDefaultValue<T>()`
- FeatureResult:
  - `GetValue<T>()`
- Feature Rule:
  - `GetVariations<T>()`
- GrowthBook:
  - `GetFeatureValue<T>(string key, T fallback)`
