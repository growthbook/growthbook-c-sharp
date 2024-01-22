using System.Runtime.CompilerServices;

// We're including this here so we can make things more testable without
// exposing unnecessary implementation details to SDK consumers.

[assembly: InternalsVisibleTo("GrowthBook.Tests")]
