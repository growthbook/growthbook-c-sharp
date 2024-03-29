using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace GrowthBook.Tests.ApiTests;

public abstract class ApiUnitTest<T> : UnitTest
{
    protected const string FirstFeatureId = nameof(FirstFeatureId);
    protected const string SecondFeatureId = nameof(SecondFeatureId);

    protected readonly ILogger<T> _logger;
    protected readonly IGrowthBookFeatureCache _cache;
    protected readonly Feature _firstFeature;
    protected readonly Feature _secondFeature;
    protected readonly Dictionary<string, Feature> _availableFeatures;

    public ApiUnitTest()
    {
        _logger = Substitute.For<ILogger<T>>();
        _cache = Substitute.For<IGrowthBookFeatureCache>();

        _firstFeature = new() { DefaultValue = 1 };
        _secondFeature = new() { DefaultValue = 2 };
        _availableFeatures = new()
        {
            [FirstFeatureId] = _firstFeature,
            [SecondFeatureId] = _secondFeature
        };
    }
}
