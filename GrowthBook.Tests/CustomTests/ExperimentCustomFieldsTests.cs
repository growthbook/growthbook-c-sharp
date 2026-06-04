using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Tests for Custom Fields functionality - GitHub issue (client request)
    /// Verifies that experiments can have custom fields defined in GrowthBook UI
    /// and that they are properly deserialized and accessible via the SDK
    /// </summary>
    public class CustomFieldsTests
    {
        [Fact]
        public void Experiment_Should_Deserialize_CustomFields()
        {
            var json = @"{
                ""key"": ""test-experiment"",
                ""variations"": [0, 1],
                ""active"": true,
                ""customFields"": {
                    ""cfl_4bzy5k3zmcjet8q5"": ""My custom field xyz"",
                    ""cfl_another_field"": ""Another value""
                }
            }";

            var experiment = JsonSerializer.Deserialize<Experiment>(json, GrowthBookJsonContext.Default.Experiment);

            experiment.Should().NotBeNull();
            experiment.CustomFields.Should().NotBeNull();
            experiment.CustomFields.Should().HaveCount(2);
            experiment.CustomFields["cfl_4bzy5k3zmcjet8q5"].ToString().Should().Be("My custom field xyz");
            experiment.CustomFields["cfl_another_field"].ToString().Should().Be("Another value");
        }

        [Fact]
        public void Experiment_Should_Handle_Missing_CustomFields()
        {
            var json = @"{
                ""key"": ""old-experiment"",
                ""variations"": [0, 1],
                ""active"": true
            }";

            var experiment = JsonSerializer.Deserialize<Experiment>(json, GrowthBookJsonContext.Default.Experiment);

            experiment.Should().NotBeNull();
            experiment.CustomFields.Should().BeNull();
        }

        [Fact]
        public void Experiment_Should_Handle_Empty_CustomFields()
        {
            var json = @"{
                ""key"": ""test-experiment"",
                ""variations"": [0, 1],
                ""customFields"": {}
            }";

            var experiment = JsonSerializer.Deserialize<Experiment>(json, GrowthBookJsonContext.Default.Experiment);

            experiment.Should().NotBeNull();
            experiment.CustomFields.Should().NotBeNull();
            experiment.CustomFields.Should().BeEmpty();
        }

        [Fact]
        public void Experiment_Should_Support_Different_CustomField_Types()
        {
            var json = @"{
                ""key"": ""test-experiment"",
                ""variations"": [0, 1],
                ""customFields"": {
                    ""cfl_string"": ""text value"",
                    ""cfl_number"": 42,
                    ""cfl_decimal"": 99.99,
                    ""cfl_bool"": true,
                    ""cfl_null"": null
                }
            }";

            var experiment = JsonSerializer.Deserialize<Experiment>(json, GrowthBookJsonContext.Default.Experiment);

            experiment.CustomFields.Should().HaveCount(5);
            experiment.CustomFields["cfl_string"].ToString().Should().Be("text value");
            ((JsonElement)experiment.CustomFields["cfl_number"]).GetInt64().Should().Be(42);
            ((JsonElement)experiment.CustomFields["cfl_decimal"]).GetDouble().Should().Be(99.99);
            ((JsonElement)experiment.CustomFields["cfl_bool"]).GetBoolean().Should().BeTrue();
            experiment.CustomFields["cfl_null"].Should().BeNull();
        }

        [Fact]
        public void GetCustomField_Should_Return_Value_When_Exists()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_field1", "value1" },
                    { "cfl_field2", 123 }
                }
            };

            var value1 = experiment.GetCustomField("cfl_field1");
            var value2 = experiment.GetCustomField("cfl_field2");

            value1.Should().Be("value1");
            value2.Should().Be(123);
        }

        [Fact]
        public void GetCustomField_Should_Return_Null_When_Not_Exists()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_exists", "value" }
                }
            };

            var value = experiment.GetCustomField("cfl_does_not_exist");

            value.Should().BeNull();
        }

        [Fact]
        public void GetCustomField_Should_Return_Null_When_CustomFields_Is_Null()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = null
            };

            var value = experiment.GetCustomField("cfl_any");

            value.Should().BeNull();
        }

        [Fact]
        public void GetCustomField_Generic_Should_Cast_To_Correct_Type()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_string", "text" },
                    { "cfl_int", 42 },
                    { "cfl_bool", true }
                }
            };

            experiment.GetCustomField<string>("cfl_string").Should().Be("text");
            experiment.GetCustomField<int>("cfl_int").Should().Be(42);
            experiment.GetCustomField<bool>("cfl_bool").Should().BeTrue();
        }

        [Fact]
        public void GetCustomField_Generic_Should_Return_Default_When_Cast_Fails()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_string", "not a number" }
                }
            };

            var value = experiment.GetCustomField<int>("cfl_string");

            value.Should().Be(0);
        }

        [Fact]
        public void HasCustomField_Should_Return_True_When_Field_Exists()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_exists", "value" }
                }
            };

            experiment.HasCustomField("cfl_exists").Should().BeTrue();
        }

        [Fact]
        public void HasCustomField_Should_Return_False_When_Field_Does_Not_Exist()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>()
            };

            experiment.HasCustomField("cfl_does_not_exist").Should().BeFalse();
        }

        [Fact]
        public void HasCustomField_Should_Return_False_When_CustomFields_Is_Null()
        {
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = null
            };

            experiment.HasCustomField("cfl_any").Should().BeFalse();
        }

        [Fact]
        public void Experiment_Equals_Should_Compare_CustomFields()
        {
            var experiment1 = new Experiment
            {
                Key = "test",
                Active = true,
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_field1", "value1" },
                    { "cfl_field2", 123 }
                }
            };

            var experiment2 = new Experiment
            {
                Key = "test",
                Active = true,
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_field1", "value1" },
                    { "cfl_field2", 123 }
                }
            };

            var experiment3 = new Experiment
            {
                Key = "test",
                Active = true,
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_field1", "different" }
                }
            };

            experiment1.Equals(experiment2).Should().BeTrue("identical custom fields should be equal");
            experiment1.Equals(experiment3).Should().BeFalse("different custom fields should not be equal");
        }

        [Fact]
        public void Experiment_Should_Serialize_CustomFields_Back_To_Json()
        {
            var experiment = new Experiment
            {
                Key = "test-experiment",
                Active = true,
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_4bzy5k3zmcjet8q5", "My custom field xyz" },
                    { "cfl_number", 42 }
                }
            };

            var json = JsonSerializer.Serialize(experiment, GrowthBookJsonContext.Default.Experiment);
            var deserialized = JsonSerializer.Deserialize<Experiment>(json, GrowthBookJsonContext.Default.Experiment);

            deserialized.CustomFields.Should().NotBeNull();
            deserialized.CustomFields.Should().HaveCount(2);
            deserialized.CustomFields["cfl_4bzy5k3zmcjet8q5"].ToString().Should().Be("My custom field xyz");
            ((JsonElement)deserialized.CustomFields["cfl_number"]).GetInt32().Should().Be(42);
        }
    }
}
