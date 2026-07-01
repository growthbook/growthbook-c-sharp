using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    public class FeaturesRefreshedEventTests
    {
        [Fact]
        public async Task LoadFeaturesWithResult_WhenModified_SetsWasModifiedAndRelaysEvent()
        {
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };
            var gb = new GrowthBook(new Context { FeatureRepository = mockRepository });

            mockRepository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(ci =>
                {
                    mockRepository.FeaturesRefreshed += Raise.EventWith(
                        new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                            new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));
                    return Task.FromResult<IDictionary<string, Feature>>(features);
                });
            FeaturesRefreshedEventArgs captured = null;
            gb.FeaturesRefreshed += (_, e) => captured = e;
            var result = await gb.LoadFeaturesWithResult(new GrowthBookRetrievalOptions {WaitForCompletion = true});
            result.Success.Should().BeTrue();
            result.WasModified.Should().BeTrue();
            captured.Should().NotBeNull();
            captured.WasModified.Should().BeTrue();
            gb.Features.Should().ContainKey("flag");
        }

        [Fact]
        public void BackgroundRefresh_WhenNotModified_DoesNotOverwriteFeatures()
        {
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };
            var gb = new GrowthBook(new Context { FeatureRepository = mockRepository });
            gb.Features.Should().BeEmpty();

            FeaturesRefreshedEventArgs captured = null;
            gb.FeaturesRefreshed += (_, e) => captured = e;

            mockRepository.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: false, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));

            gb.Features.Should().BeEmpty();
            captured.Should().NotBeNull();
            captured.WasModified.Should().BeFalse();

        }
    }
}
