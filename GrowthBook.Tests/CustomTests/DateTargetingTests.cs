using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Tests for date targeting functionality improvements - GitHub issue #46
    /// Verifies that comparisons order ISO dates as text, numbers numerically, and never coerce strings to numbers
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
                Attributes = JObject.FromObject(new { signupDate = today.ToString("yyyy-MM-dd") }),
                Features = new Dictionary<string, Feature>
                {
                    ["date-feature"] = new Feature
                    {
                        DefaultValue = false,
                        Rules = new List<FeatureRule>
                        {
                            new FeatureRule
                            {
                                Condition = JObject.Parse($@"{{
                                    ""signupDate"": {{
                                        ""$gt"": ""{yesterday:yyyy-MM-dd}""
                                    }}
                                }}"),
                                Force = true
                            }
                        }
                    }
                },
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBook = new GrowthBookSdk.GrowthBook(context);
            
            var result = growthBook.IsOn("date-feature");
            result.Should().BeTrue("ISO dates order correctly when compared as text");
        }

        [Fact]
        public void NumericComparison_Should_Compare_Numbers_Numerically_And_Strings_As_Text()
        {
            var context = new Context
            {
                Enabled = true,
                Attributes = JObject.FromObject(new { 
                    userAge = 25,
                    priceString = "99.50"
                }),
                Features = new Dictionary<string, Feature>
                {
                    ["age-feature"] = new Feature
                    {
                        DefaultValue = false,
                        Rules = new List<FeatureRule>
                        {
                            new FeatureRule
                            {
                                Condition = JObject.Parse(@"{
                                    ""userAge"": {
                                        ""$gte"": 21
                                    }
                                }"),
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
                                Condition = JObject.Parse(@"{
                                    ""priceString"": {
                                        ""$lt"": ""100.00""
                                    }
                                }"),
                                Force = true
                            }
                        }
                    }
                },
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBook = new GrowthBookSdk.GrowthBook(context);
            
            growthBook.IsOn("age-feature").Should().BeTrue("integers should be compared numerically");
            growthBook.IsOn("price-feature").Should().BeFalse("numeric strings compare as text, and \"99.50\" sorts after \"100.00\"");
        }

        [Fact]
        public void StringComparison_Should_Not_Depend_On_Culture()
        {
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            var originalUICulture = Thread.CurrentThread.CurrentUICulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("fr-FR");

            try
            {
                var context = new Context
                {
                    Enabled = true,
                    Attributes = JObject.FromObject(new { name = "B" }),
                    Features = new Dictionary<string, Feature>
                    {
                        ["name-feature"] = new Feature
                        {
                            DefaultValue = false,
                            Rules = new List<FeatureRule>
                            {
                                new FeatureRule
                                {
                                    Condition = JObject.Parse(@"{
                                        ""name"": {
                                            ""$lt"": ""a""
                                        }
                                    }"),
                                    Force = true
                                }
                            }
                        }
                    },
                    LoggerFactory = NullLoggerFactory.Instance
                };

                using var growthBook = new GrowthBookSdk.GrowthBook(context);

                growthBook.IsOn("name-feature").Should().BeTrue("strings compare by character code, so \"B\" < \"a\" whatever the process culture");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                Thread.CurrentThread.CurrentUICulture = originalUICulture;
            }
        }

    }
}
