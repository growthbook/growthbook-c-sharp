# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Fixed `Context.EncryptedFeatures` never being decrypted. The property was declared, documented and cloned, but
  nothing ever read it, so a caller who supplied an encrypted payload was left with an empty feature set and every
  flag reading as false - with nothing to distinguish a wrong key from a working one. The payload is now decrypted in
  the constructor and takes precedence over `Context.Features`. A payload supplied without `Context.DecryptionKey`
  throws `ArgumentException`, and one that will not decrypt or will not parse throws `DecryptionException`, rather
  than failing silently.
- Fixed the async evaluation methods ignoring a locally supplied payload. `IsOnAsync`, `IsOffAsync`, `EvalFeatureAsync`
  and `GetFeatureValueAsync` always loaded from the repository first, so a caller evaluating an offline payload hit an
  API they never configured - with no client key that is a failing request to the default host, and a response that did
  arrive replaced the payload they supplied. The load now happens only when there is somewhere to load from: a client
  key, or a repository the caller provided.
- Stopped writing the decryption key to the log. It was logged verbatim at debug level on both feature fetch paths,
  so anyone with log access could decrypt the payloads it protects. Rotate the key if debug logging has been enabled
  where those logs are retained or shipped elsewhere.
- Stopped logging feature payload bodies, both the raw API response and the decrypted one. A payload carries saved
  groups, which are typically lists of user identifiers, so it is personal data. Character counts are logged instead.

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
