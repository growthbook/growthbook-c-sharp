using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Tests for date targeting functionality improvements - GitHub issue #46
    /// Verifies that the EvaluateComparison fix properly handles DateTime and numeric parsing
    /// </summary>
    public class DateTargetingTests
    {
        [Fact]
        public void DateComparison_Should_Work_With_DateTime_Parsing()
        {
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);

            var context = new Context
            {
                Enabled = true,
                Attributes = new JsonObject
                {
                    ["signupDate"] = today.ToString("yyyy-MM-dd")
                },
                Features = new Dictionary<string, Feature>
                {
                    ["date-feature"] = new Feature
                    {
                        DefaultValue = false,
                        Rules = new List<FeatureRule>
                        {
                            new FeatureRule
                            {
                                Condition = new JsonObject
                                {
                                    ["signupDate"] = new JsonObject
                                    {
                                        ["$gt"] = yesterday.ToString("yyyy-MM-dd")
                                    }
                                },
                                Force = true
                            }
                        }
                    }
                },
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBook = new GrowthBookSdk.GrowthBook(context);

            var result = growthBook.IsOn("date-feature");
            result.Should().BeTrue("dates should be parsed and compared as DateTime objects");
        }

        [Fact]
        public void NumericComparison_Should_Work_With_Number_Parsing()
        {
            var context = new Context
            {
                Enabled = true,
                Attributes = new JsonObject
                {
                    ["userAge"] = 25,
                    ["priceString"] = "99.50"
                },
                Features = new Dictionary<string, Feature>
                {
                    ["age-feature"] = new Feature
                    {
                        DefaultValue = false,
                        Rules = new List<FeatureRule>
                        {
                            new FeatureRule
                            {
                                Condition = new JsonObject
                        {
                            ["userAge"] = new JsonObject
                            {
                                ["$gte"] = 21
                            }
                        },
                                Force = true
                            }
                        }
                    },
                    ["price-feature"] = new Feature
                    {
                        DefaultValue = false,
                        Rules = new List<FeatureRule>
                        {
                            new FeatureRule
                            {
                                Condition = new JsonObject
                        {
                            ["priceString"] = new JsonObject
                            {
                                ["$lt"] = "100.00"
                            }
                        },
                                Force = true
                            }
                        }
                    }
                },
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBook = new GrowthBookSdk.GrowthBook(context);

            growthBook.IsOn("age-feature").Should().BeTrue("integers should be compared numerically");
            growthBook.IsOn("price-feature").Should().BeTrue("numeric strings should be parsed and compared as numbers");
        }

    }
}