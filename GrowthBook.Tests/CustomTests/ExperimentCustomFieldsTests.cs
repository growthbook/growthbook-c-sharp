using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json;
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
            // Arrange - JSON from actual GrowthBook API response
            var json = @"{
                ""key"": ""test-experiment"",
                ""variations"": [0, 1],
                ""active"": true,
                ""customFields"": {
                    ""cfl_4bzy5k3zmcjet8q5"": ""My custom field xyz"",
                    ""cfl_another_field"": ""Another value""
                }
            }";

            // Act
            var experiment = JsonConvert.DeserializeObject<Experiment>(json);

            // Assert
            experiment.Should().NotBeNull();
            experiment.CustomFields.Should().NotBeNull();
            experiment.CustomFields.Should().HaveCount(2);
            experiment.CustomFields["cfl_4bzy5k3zmcjet8q5"].Should().Be("My custom field xyz");
            experiment.CustomFields["cfl_another_field"].Should().Be("Another value");
        }

        [Fact]
        public void Experiment_Should_Handle_Missing_CustomFields()
        {
            // Arrange - Experiment without customFields (backward compatibility)
            var json = @"{
                ""key"": ""old-experiment"",
                ""variations"": [0, 1],
                ""active"": true
            }";

            // Act
            var experiment = JsonConvert.DeserializeObject<Experiment>(json);

            // Assert
            experiment.Should().NotBeNull();
            experiment.CustomFields.Should().BeNull();
        }

        [Fact]
        public void Experiment_Should_Handle_Empty_CustomFields()
        {
            // Arrange
            var json = @"{
                ""key"": ""test-experiment"",
                ""variations"": [0, 1],
                ""customFields"": {}
            }";

            // Act
            var experiment = JsonConvert.DeserializeObject<Experiment>(json);

            // Assert
            experiment.Should().NotBeNull();
            experiment.CustomFields.Should().NotBeNull();
            experiment.CustomFields.Should().BeEmpty();
        }

        [Fact]
        public void Experiment_Should_Support_Different_CustomField_Types()
        {
            // Arrange - Different data types in custom fields
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

            // Act
            var experiment = JsonConvert.DeserializeObject<Experiment>(json);

            // Assert
            experiment.CustomFields.Should().HaveCount(5);
            experiment.CustomFields["cfl_string"].Should().Be("text value");
            experiment.CustomFields["cfl_number"].Should().Be(42L);
            experiment.CustomFields["cfl_decimal"].Should().Be(99.99);
            experiment.CustomFields["cfl_bool"].Should().Be(true);
            experiment.CustomFields["cfl_null"].Should().BeNull();
        }

        [Fact]
        public void GetCustomField_Should_Return_Value_When_Exists()
        {
            // Arrange
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_field1", "value1" },
                    { "cfl_field2", 123 }
                }
            };

            // Act
            var value1 = experiment.GetCustomField("cfl_field1");
            var value2 = experiment.GetCustomField("cfl_field2");

            // Assert
            value1.Should().Be("value1");
            value2.Should().Be(123);
        }

        [Fact]
        public void GetCustomField_Should_Return_Null_When_Not_Exists()
        {
            // Arrange
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_exists", "value" }
                }
            };

            // Act
            var value = experiment.GetCustomField("cfl_does_not_exist");

            // Assert
            value.Should().BeNull();
        }

        [Fact]
        public void GetCustomField_Should_Return_Null_When_CustomFields_Is_Null()
        {
            // Arrange
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = null
            };

            // Act
            var value = experiment.GetCustomField("cfl_any");

            // Assert
            value.Should().BeNull();
        }

        [Fact]
        public void GetCustomField_Generic_Should_Cast_To_Correct_Type()
        {
            // Arrange
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

            // Act & Assert
            experiment.GetCustomField<string>("cfl_string").Should().Be("text");
            experiment.GetCustomField<int>("cfl_int").Should().Be(42);
            experiment.GetCustomField<bool>("cfl_bool").Should().BeTrue();
        }

        [Fact]
        public void GetCustomField_Generic_Should_Return_Default_When_Cast_Fails()
        {
            // Arrange
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_string", "not a number" }
                }
            };

            // Act
            var value = experiment.GetCustomField<int>("cfl_string");

            // Assert
            value.Should().Be(0); // default(int)
        }

        [Fact]
        public void HasCustomField_Should_Return_True_When_Field_Exists()
        {
            // Arrange
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>
                {
                    { "cfl_exists", "value" }
                }
            };

            // Act
            var hasField = experiment.HasCustomField("cfl_exists");

            // Assert
            hasField.Should().BeTrue();
        }

        [Fact]
        public void HasCustomField_Should_Return_False_When_Field_Does_Not_Exist()
        {
            // Arrange
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = new Dictionary<string, object>()
            };

            // Act
            var hasField = experiment.HasCustomField("cfl_does_not_exist");

            // Assert
            hasField.Should().BeFalse();
        }

        [Fact]
        public void HasCustomField_Should_Return_False_When_CustomFields_Is_Null()
        {
            // Arrange
            var experiment = new Experiment
            {
                Key = "test",
                CustomFields = null
            };

            // Act
            var hasField = experiment.HasCustomField("cfl_any");

            // Assert
            hasField.Should().BeFalse();
        }

        [Fact]
        public void Experiment_Equals_Should_Compare_CustomFields()
        {
            // Arrange
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

            // Act & Assert
            experiment1.Equals(experiment2).Should().BeTrue("identical custom fields should be equal");
            experiment1.Equals(experiment3).Should().BeFalse("different custom fields should not be equal");
        }

        [Fact]
        public void Experiment_Should_Serialize_CustomFields_Back_To_Json()
        {
            // Arrange
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

            // Act
            var json = JsonConvert.SerializeObject(experiment);
            var deserialized = JsonConvert.DeserializeObject<Experiment>(json);

            // Assert
            deserialized.CustomFields.Should().NotBeNull();
            deserialized.CustomFields.Should().HaveCount(2);
            deserialized.CustomFields["cfl_4bzy5k3zmcjet8q5"].Should().Be("My custom field xyz");
            deserialized.CustomFields["cfl_number"].Should().Be(42L);
        }
    }
}
