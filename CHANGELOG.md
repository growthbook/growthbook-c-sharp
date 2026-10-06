# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Added forced features, via `Context.ForcedFeatures` and `GrowthBook.SetForcedFeatures(...)`. A forced value
  short-circuits evaluation for that key and is reported with the `override` source, matching the reference SDKs.
- Added `FeatureResult.RuleId`, carrying the id of the rule that produced the result, so a caller can tell which
  rule matched rather than only what it returned. Set for the `force` and `experiment` sources, where the
  reference SDK passes `rule.id`, and left empty everywhere else.
- Added sticky bucket assignments being refreshed when the attributes they are keyed on change, instead of
  staying on the identifier the instance was created with. Previously `IStickyBucketService.GetAllAssignments`
  was declared but never called, so an instance only ever had whatever docs the caller pre-populated.
- Added `IAsyncStickyBucketService` and `Context.AsyncStickyBucketService`, for backing stores that are async by
  nature. Implementing the synchronous `IStickyBucketService` over such a store forces a blocking
  `.GetAwaiter().GetResult()` inside evaluation, which deadlocks under a captured synchronization context.
  Evaluation itself stays synchronous: assignments are read up front by
  `GrowthBook.LoadStickyBucketAssignmentsAsync(...)`, which `LoadFeatures` also calls, and writes are dispatched
  without being awaited. Setting both services throws at construction, since it would be ambiguous which store
  owns an assignment.
- Added `RedisStickyBucketService`, an `IAsyncStickyBucketService` backed by Redis. It talks to
  `IRedisCompatibleClient` rather than to a specific client library, so no Redis dependency is added to the
  package, and it reads a whole round of assignments in one batch call.
- Added `Context.Clone()`.
- Fixed the case-insensitive comparison operators degrading to plain equality when the condition value was an
  operator object. They now evaluate the nested operator with the comparison applied, rather than testing the
  attribute against the object itself.
- Fixed `MergeAttributes` mutating the attributes in place, which could expose a partially merged state to a
  concurrent evaluation, and fixed it throwing on null values. A null value is stored as a JSON null rather than
  removing the key, matching the TypeScript, PHP, Ruby and Swift SDKs.
- Added `UpdateAttributes`/`MergeAttributes` to `IGrowthBook`.
- Added `UpdateAttributesAsync`/`MergeAttributesAsync`, which wait for the remote evaluation that an attribute
  change triggers. The synchronous versions no longer leave that evaluation unobserved: the next feature load
  waits for it, so a stale in-flight response can't overwrite newer features.
- Fixed a superseded remote evaluation overwriting a newer one. Each evaluation is now tagged with a generation
  allocated together with the state it sends, and only the most recent one publishes its features, so a response
  built for attributes that have already been replaced is dropped instead of applied.
- Fixed changes to `ForcedVariations` not refreshing remotely evaluated features. They're part of the remote
  evaluation payload, so assigning the property now triggers an evaluation the same way attribute changes do, and
  `SetForcedVariations`/`SetForcedVariationsAsync` were added for callers that need to wait for it. Assigning
  `Attributes` directly triggers one as well, rather than only `UpdateAttributes`/`MergeAttributes` doing so.
- Added retries with exponential backoff to remote evaluation requests, so a single transient failure no longer
  leaves a remote-eval consumer on the previously evaluated features until something else triggers a refresh.
  Transport errors, timeouts, 408, 429 and 5xx are retried (honouring `Retry-After`); other 4xx are not, since
  repeating a rejected request only fails the same way. Configurable through `RemoteEvaluationRetryPolicy`;
  defaults to 3 attempts, 500ms initial backoff, capped at 5s. The whole round is bounded by a 60s budget, so
  retrying can't hold a waiting caller for longer than a single request already could.
- Stopped logging the remote evaluation request and response bodies at debug level. The request is built from the
  user's attributes, which are personal data, and an evaluated response can carry saved groups, which are typically
  lists of user identifiers. Counts are logged instead.
- Fixed a sticky bucket load that was overtaken by a newer one still publishing its documents. Loads overlap
  whenever the identifier changes while one is in flight, and the earlier one holds the previous identifier's
  documents however late it arrives, so it is now dropped rather than replacing the current user's.
- Fixed a newly stored sticky bucket assignment being written into the dictionary that concurrent evaluations are
  reading. It is published as a replacement set instead, the same way a refresh already was.
- Fixed an experiment passed directly to `Run` not finding its stored assignment. The documents loaded up front
  are derived from the loaded features and `Context.Experiments`, so an experiment listed in neither was bucketed
  as though nothing had been stored for it. Applies to the synchronous store; with an asynchronous one, call
  `LoadStickyBucketAssignmentsAsync` after adding the experiment.
- Fixed `RedisStickyBucketService` reporting a cancelled read as an empty set of assignments, which had the caller
  replace the assignments it still held with nothing. Cancellation now propagates.
- Fixed `RedisStickyBucketService` publishing a stored document under the identifier named inside its JSON rather
  than the key it was read from, so an entry written against one identifier could hand out another user's
  variation. A document that doesn't match the key it was read under is ignored.
- Fixed a sticky bucket store that could not be read reporting an empty set of assignments, which had the caller
  replace everything it held with nothing and unstick every user it had already bucketed. A failed read now
  answers `null`, which `IAsyncStickyBucketService.GetAllAssignmentsAsync` defines as "no answer" and which
  leaves the loaded assignments in place. Previously only a cancelled read was distinguished this way.
- Fixed a sticky bucket store failure turning a successful feature load into a reported failure. The features are
  applied before the assignments are refreshed, so the refresh no longer decides whether the load succeeded.
- Forced features are now part of the remote evaluation payload, and changing them triggers a re-evaluation the
  same way changing attributes or forced variations does. A forced value short-circuits evaluation of its own
  feature locally either way, but a feature that reaches a forced one through a prerequisite is resolved
  server-side, and applying the override locally afterwards cannot correct that result. Added
  `SetForcedFeaturesAsync` for callers that need to wait for the re-evaluation.

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
