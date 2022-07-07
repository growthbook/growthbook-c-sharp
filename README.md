# growthbook-c-sharp
Powerful Feature flagging and A/B testing for C# apps using [GrowthBook](https://www.growthbook.io/)

## Usage
This library is based on the [GrowthBook SDK specs](https://docs.growthbook.io/lib/build-your-own) and should be compatible
with the usage examples in the [GrowthBook docs](https://docs.growthbook.io/).

Because feature definitions are typically loaded from API calls or cache, [Json.NET](https://www.nuget.org/packages/Newtonsoft.Json/13.0.2-beta1)
objects are used to represent arbitrary document types such as Attributes, Conditions, and Feature values.

To make it easier to deal with Feature values, generic getter functions are provided for the following:

- Experiment:
	- GetVariations<T>()
- ExperimentResult:
	- GetValue<T>()
- Feature:
	- GetDefaultValue<T>()
- FeatureResult:
	- GetValue<T>()
- Feature Rule:
	- GetVariations<T>()
- GrowthBook:
	- GetFeatureValue<T>(string key, T fallback)