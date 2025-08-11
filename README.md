# GrowthBook - SDK (C#)

![growthbook banner with csharp logo](https://camo.githubusercontent.com/b6cc3335dcf09b9c3baf421e28ded771ae34a5efaf87b794eef364f10384d904/68747470733a2f2f646f63732e67726f777468626f6f6b2e696f2f696d616765732f6865726f2d6373686172702d73646b2e706e67)

Powerful feature flagging and A/B testing for C# apps using [GrowthBook](https://www.growthbook.io/).

[![nuget badge for growthbook](https://img.shields.io/nuget/v/growthbook-c-sharp?style=flat-square)](https://www.nuget.org/packages/growthbook-c-sharp)

## Table of Contents

- [Overview](#overview)
- [Installation](#installation)
- [Integration](#integration)
- [Usage Guide](#usage-guide)
- [Sticky Bucketing](#sticky-bucketing)
- [Models](#models)
- [Streaming Updates](#streaming-updates)
- [Remote Evaluation](#remote-evaluation)
- [License](#license)

---

## Overview

- **Lightweight and fast**
- **Supports all platforms via C#**
- **Latest spec version: 0.7.0 [View Changelog](https://docs.growthbook.io/lib/build-your-own#changelog)**

---

## Installation


1. Install GrowthBook

```csharp
dotnet add package growthbook-c-sharp
```
---

## Integration

1. Declare features

    ```csharp
    var staticFeatures = new Dictionary<string, Feature>
        {
            {"firstFeature", new Feature{ DefaultValue = true}},
            {"secondFeature", new Feature{ DefaultValue = false}}
        };
    ```

2. Create a context and add the features

    ```csharp
    var context = new Context
    {
        Enabled = true,
        Url = "",
        Features = staticFeatures
    };
    ```
3. Create the `GrowthBook` object with the context

    ```csharp
        using GrowthBook;
    //...

    var GrowthBook = new GrowthBook.GrowthBook(context);
    ```

4. Check whether a feature is enabled

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

---

## Usage Guide

---

### 1. **Feature Flag Evaluation**
Evaluate feature flags to determine their state or retrieve their values.

- **Check if a Feature is Enabled**:
  ```csharp
  bool isFeatureEnabled = growthBook.IsOn("feature-key");
  ```

- **Retrieve Feature Values**:
  ```csharp
  string theme = growthBook.GetFeatureValue("app-theme");
  ```

- **Evaluate a Feature with Detailed Results**:
  ```csharp
  var result = growthBook.EvalFeature("new-onboarding-flow");
  if (result.On)
  {
      // Enable new onboarding flow
  }
  ```

---

### 2. **Experiment Management**
Run A/B tests and manage experiment assignments.

- **Run an Experiment**:
  ```csharp
  var experiment = new Experiment
  {
      Key = "button-color",
      Variations = new object[] { "red", "blue" },
      Weights = new float[] { 0.5f, 0.5f },
      Coverage = 0.2f
  };
  var result = growthBook.Run(experiment);
  ```

---

### 3. **Attribute Management**
Update or merge user attributes dynamically to reflect changes in targeting or experiment assignment.

- **Update Attributes**:
  ```csharp
  growthBook.UpdateAttributes(new { id = "user123", country = "US" });
  ```

- **Merge Additional Attributes**:
  ```csharp
  growthBook.MergeAttributes(new { age = 30 });
  ```

---

### 4. **Lifecycle Management**
Manage the lifecycle of the `GrowthBook` instance, including cleanup and resource disposal.

- **Dispose of Resources**:
  ```csharp
  growthBook.Destroy();
  ```

---

### 5. **Sticky Bucketing**

```csharp
context.StickyBucketService = new StickyBucketService();
```
or create it when initializing

---

## Sticky Bucketing
Implement a `StickyBucketService`:

```csharp
public class StickyBucketService
{
    public Dictionary<string, object> GetGroups(string userId) => LoadFromStorage(userId);
}
```

```csharp
context.StickyBucketService = new StickyBucketService();
```

or pass it during initialization

Sticky bucketing ensures that users see the same experiment variant, even when user session, user login status, or experiment parameters change. See the [Sticky Bucketing docs](/app/sticky-bucketing) for more information. If your organization and experiment supports sticky bucketing, you must implement an instance of the `StickyBucketService` to use Sticky Bucketing. For simple bucket persistence using the browser's LocalStorage (can be polyfilled for other environments).

---

## Models

The GrowthBook C# SDK uses a set of core models to define the context, features, experiments, and results. These models are essential for configuring the SDK, evaluating feature flags, and running experiments. Below is the documentation for the key models used in the SDK.

---

### `Context Model`

Defines the runtime context for feature evaluation. This includes API configuration, user attributes, feature definitions, and other settings that control the behavior of the GrowthBook instance.

```csharp
public class Context
{
    /// Switch to globally disable all experiments. Default is true.
    public bool Enabled { get; set; } = true;

    /// The GrowthBook API Host. Optional.
    public string ApiHost { get; set; }

    /// The key used to fetch features from the GrowthBook API. Optional.
    public string ClientKey { get; set; }

    /// The key used to decrypt encrypted features from the API. Optional.
    public string DecryptionKey { get; set; }

    /// Map of user attributes that are used to assign variations.
    public JObject Attributes { get; set; } = new JObject();

    /// The URL of the current page.
    public string Url { get; set; }

    /// Feature definitions (usually pulled from an API or cache).
    public IDictionary<string, Feature> Features { get; set; } = new Dictionary<string, Feature>();

    /// Experiment definitions. Optional.
    public IList<Experiment> Experiments { get; set; }

    /// Service for implementing sticky bucketing to ensure consistent experiment assignments.
    public IStickyBucketService StickyBucketService { get; set; }

    /// Assignment documents for sticky bucket usage. Optional.
    public IDictionary<string, StickyAssignmentsDocument> StickyBucketAssignmentDocs { get; set; } = new Dictionary<string, StickyAssignmentsDocument>();

    /// Feature definitions that have been encrypted. Requires DecryptionKey to decrypt.
    public string EncryptedFeatures { get; set; }

    /// Forces specific experiments to always assign a specific variation (used for QA).
    public IDictionary<string, int> ForcedVariations { get; set; } = new Dictionary<string, int>();

    /// Saved groups for sticky bucketing or other purposes. Optional.
    public JObject SavedGroups { get; set; }

    /// If true, random assignment is disabled, and only explicitly forced variations are used.
    public bool QaMode { get; set; } = false;

    /// Callback function used for tracking experiment assignments.
    public Action<Experiment, ExperimentResult> TrackingCallback { get; set; }

    /// A repository implementation for retrieving and caching features. Optional.
    public IGrowthBookFeatureRepository FeatureRepository { get; set; }

    /// A logger factory implementation that will enable logging throughout the SDK. Optional.
    public ILoggerFactory LoggerFactory { get; set; }

    /// Custom cache directory path for the cache manager. Uses system temp directory if not specified.
    public string CachePath { get; set; }
}
```

---

### `Experiment Model`

Represents a single experiment with multiple variations. This class defines the structure and behavior of an experiment, including its variations, targeting conditions, bucketing logic, and other metadata.

```csharp
public class Experiment
{
    /// The globally unique identifier for the experiment.
    public string Key { get; set; }

    /// The different variations to choose between.
    public JArray Variations { get; set; }

    /// How to weight traffic between variations. Must add to 1.
    public IList<double> Weights { get; set; }

    /// If set to false, always return the control (first variation).
    public bool Active { get; set; } = true;

    /// What percent of users should be included in the experiment (between 0 and 1, inclusive).
    public double? Coverage { get; set; }

    /// Array of ranges, one per variation.
    public IList<BucketRange> Ranges { get; set; }

    /// Optional targeting condition.
    public JObject Condition { get; set; }

    /// Each item defines a prerequisite where a condition must evaluate against a parent feature's value (identified by id). If gate is true, then this is a blocking feature-level prerequisite; otherwise it applies to the current rule only.
    public IList<ParentCondition> ParentConditions { get; set; }

    /// Adds the experiment to a namespace.
    public Namespace Namespace { get; set; }

    /// All users included in the experiment will be forced into the specific variation index.
    public int? Force { get; set; }

    /// What user attribute should be used to assign variations (defaults to id).
    public string HashAttribute { get; set; } = "id";

    /// When using sticky bucketing, can be used as a fallback to assign variations.
    public string FallbackAttribute { get; set; }

    /// The hash version to use (defaults to 1).
    public int HashVersion { get; set; } = 1;

    /// Meta info about the variations.
    public IList<VariationMeta> Meta { get; set; }

    /// Array of filters to apply.
    public IList<Filter> Filters { get; set; }

    /// The hash seed to use.
    public string Seed { get; set; }

    /// Human-readable name for the experiment.
    public string Name { get; set; }

    /// ID of the current experiment phase.
    public string Phase { get; set; }

    /// If true, sticky bucketing will be disabled for this experiment. (Note: sticky bucketing is only available if a StickyBucketingService is provided in the Context).
    public bool DisableStickyBucketing { get; set; }

    /// A sticky bucket version number that can be used to force a re-bucketing of users (default to 0).
    public int BucketVersion { get; set; } = 0;

    /// Any users with a sticky bucket version less than this will be excluded from the experiment.
    public int MinBucketVersion { get; set; } = 0;

    /// Any URL patterns associated with this experiment.
    public IList<UrlPattern> UrlPatterns { get; set; }

    /// Determines whether to persist the query string.
    public bool PersistQueryString { get; set; }
}
```

---

### `ExperimentResult Model`


The result of running an experiment given a specific context.

```csharp
public class ExperimentResult
{
    Whether or not the user is part of the experiment.
    public bool InExperiment { get; set; }

    The array index of the assigned variation.
    public int VariationId { get; set; }

    The array value of the assigned variation.
    public JToken Value { get; set; } = JValue.CreateNull();

    If a hash was used to assign a variation.
    public bool HashUsed { get; set; }

    The user attribute used to assign a variation.
    public string HashAttribute { get; set; } = string.Empty;

    The value of that attribute.
    public string HashValue { get; set; } = string.Empty;

    The id of the feature (if any) that the experiment came from.
    public string FeatureId { get; set; }

    The unique key for the assigned variation.
    public string Key { get; set; }

    The hash value used to assign a variation (float from 0 to 1).
    public double Bucket { get; set; }

    The human-readable name of the assigned variation.
    public string Name { get; set; }

    Used for holdout groups.
    public bool Passthrough { get; set; }

    If sticky bucketing was used to assign a variation.
    public bool StickyBucketUsed { get; set; }
}
```

---

### `Feature Model`


Represents an object consisting of a default value plus rules that can override the default.

```csharp
public class Feature
{
    The default value (should use null if not specified).
    public JToken DefaultValue { get; set; }

    Array of FeatureRule objects that determine when and how the defaultValue gets overridden.
    public IList<FeatureRule> Rules { get; set; }
}
```

---

### `FeatureResult Model`

Represents the result of evaluating a feature flag, including its value, source, and associated experiment details (if applicable).

```csharp
public class FeatureResult
{
    public static class SourceId
    {
        //Represents the source when the queried feature doesn't exist in GrowthBook.
        public const string UnknownFeature = "unknownFeature";

        //Represents the source when the default value for the feature is being processed.
        public const string DefaultValue = "defaultValue";

        //Represents the source when the feature value is forced.
        public const string Force = "force";

        //Represents the source when the feature value comes from an active experiment.
        public const string Experiment = "experiment";

        //Represents the source when there is a cyclic prerequisite in the evaluation.
        public const string CyclicPrerequisite = "cyclicPrerequisite";

        //Represents the source when a prerequisite condition fails.
        public const string Prerequisite = "prerequisite";
    }

    //The assigned value of the feature.
    public JToken Value { get; set; }

    //The assigned value cast to a boolean. Returns `true` if the value is non-null, non-empty, and not equivalent to "0" or "false".
    public bool On

    //The assigned value cast to a boolean and then negated.
    public bool Off { get { return !On; } }

    //One of "unknownFeature", "defaultValue", "force", "experiment", or "cyclicPrerequisite". Indicates the source of the feature value.
    public string Source { get; set; }

    //When the source is "experiment", this will be the associated `Experiment` object.
    public Experiment Experiment { get; set; }

    //When the source is "experiment", this will be the associated `ExperimentResult` object.
    public ExperimentResult ExperimentResult { get; set; }
}
```

---

### `FeatureRule`

```csharp
public class FeatureRule
{
    //Optional rule id, reserved for future use.
    public string Id { get; set; }

    //Optional targeting condition.
    public JObject Condition { get; set; }

    //Each item defines a prerequisite where a condition must evaluate against a parent feature's value (identified by id). If `gate` is true, then this is a blocking feature-level prerequisite; otherwise, it applies to the current rule only.
    public IList<ParentCondition> ParentConditions { get; set; }

    //What percent of users should be included in the experiment (between 0 and 1, inclusive).
    public double? Coverage { get; set; }

    //Immediately force a specific value (ignore every other option besides `condition` and `coverage`).
    public JToken Force { get; set; }

    //Run an experiment (A/B test) and randomly choose between these variations.
    public JArray Variations { get; set; }

    //The globally unique tracking key for the experiment (defaults to the feature key).
    public string Key { get; set; }

    //How to weight traffic between variations. Must add to 1.
    public IList<double> Weights { get; set; }

    //Adds the experiment to a namespace.
    public Namespace Namespace { get; set; }

    //What user attribute should be used to assign variations (defaults to `id`).
    public string HashAttribute { get; set; } = "id";

    //When using sticky bucketing, can be used as a fallback to assign variations.
    public string FallbackAttribute { get; set; }

    //The hash version to use (defaults to 1).
    public int HashVersion { get; set; } = 1;

    //A more precise version of `Coverage`.
    public BucketRange Range { get; set; }

    //If true, sticky bucketing will be disabled for this experiment. Sticky bucketing is only available if a `StickyBucketingService` is provided in the `Context`.
    public bool DisableStickyBucketing { get; set; }

    //A sticky bucket version number that can be used to force a re-bucketing of users (defaults to 0).
    public int BucketVersion { get; set; }

    //Any users with a sticky bucket version less than this will be excluded from the experiment.
    public int MinBucketVersion { get; set; }

    //Ranges for experiment variations.
    public IList<BucketRange> Ranges { get; set; }

    //Meta info about the experiment variations.
    public IList<VariationMeta> Meta { get; set; }

    //Array of filters to apply to the rule.
    public IList<Filter> Filters { get; set; }

    //Seed to use for hashing.
    public string Seed { get; set; }

    //Human-readable name for the experiment.
    public string Name { get; set; }

    //The phase id of the experiment.
    public string Phase { get; set; }

    //Array of tracking calls to fire.
    public IList<TrackData> Tracks { get; set; }
}
```

---

### `TrackData Model`

Represents the track data associated with a feature rule. This class is used to encapsulate tracking information for experiments and their results.

```csharp
    public class TrackData
    {
        //The tracked experiment.
        public Experiment Experiment { get; set; }

        //The tracked experiment result.
        public ExperimentResult Result { get; set; }
    }
```

---

## License

This project uses the **MIT License**. The core GrowthBook application will always remain open and free, though commercial enterprise add-ons may be introduced in the future.
