# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Added consumption of the contextual bandit payload, so a feature rule referencing a contextual bandit is bucketed
  with the backend-computed weights of whichever segment the user falls into, instead of being skipped.
  `contextualBandits` and `encryptedContextualBandits` are read from the feature payload on all three delivery paths
  (polling, server-sent events, remote evaluation), and can also be supplied directly through
  `Context.ContextualBandits`. `ExperimentResult` reports `LeafId`, `VariationWeights` and `BanditVersion` for a real
  exposure, and nothing for a forced, QA-mode or filtered-out assignment.
- Added `IGrowthBookContextualBanditSource`, implemented by the built-in repository and refresh worker. It is a
  separate interface rather than new members on `IGrowthBookFeatureRepository` because this library targets
  netstandard2.0, which has no default interface members, so extending the existing one would break every consumer
  that implements it. A custom repository keeps working and supplies no bandits until it opts in.
- Updated the vendored spec suite to 0.8.0 and wired its new `contextualBandit` section (35 cases). The nine
  condition cases this fork had added to that file moved to `custom-cases.json`, where a future spec bump can't
  delete them.

## [1.2.0]

- Added custom fields support for experiments.
- Added non-breaking ETag caching support for feature API refreshes.
- Fixed sticky bucket min bucket version handling.
- Fixed invariant numeric condition parsing.
- Fixed bucket range serialization.
- Improved CI build, test, and packaging coverage.

## [1.1.0]

- Upated SDK to comply with the 0.7.0 SDK spec.

## [1.0.7]

- Added async versions of EvalFeature/GetFeatureValue and a flag on the originals for backwards compatibility.

## [1.0.6]

- Set hash version with rule

## [1.0.5]

- Fixed missing readme image to use trusted domain (when viewed from nuget.org)

## [1.0.4]

- Added package readme reference

## [1.0.3]

- Fixed null reference exception when forcing a rule with a tracking callback set and null tracking data.
- Experiment assignments are included in EvalFeature as well as Run.
- IGrowthBook interface fixed to inherit IDisposable.

## [1.0.2]

- Fixed issue with incorrect logic in GrowthBook.GetFeatureResult<T>() call.

## [1.0.1]

- Fixed issue with empty string value sent to IsIn condition evaluation.

## [1.0.0]

- Fully implemented version 0.5.2 of the GrowthBook SDK spec.
- Added support for retrieving features (both regular and encrypted) from the GrowthBook API with in-memory caching.
- Added support for retrieving features (both regular and encrypted) in near-realtime with Server Sent Events (when preferred and available).
- Added extensive support for logging.
- Added more robust error handling.
- New unit test structure for easier use of the standard cases.json test suite.

## [0.2.0]

- Corrected name of `IGrowthbook` to `IGrowthBook`

## [0.1.2]

- ci: moved from MSTest to Xunit

## [0.1.1]

- Handle null namespace property.

## [0.1.0]

- Added a CHANGELOG.md based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)
- Added standard rules for markdown files in .editorconfig
- Ensured that all files have a consistent line-ending (based on what they already have)
- Added `IGrowthBook` interface

## [0.0.6] - 2022-06-07

- Correct package repo

## [0.0.5] - 2022-06-07

- Update package repository link, bump version number for new license inclusion

## [0.0.4] - 2022-06-07

- Initial upload
