using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Which saved groups <see cref="PayloadReducer.ReducePayload"/> reduces and which it keeps as written, for payloads
/// larger or deeper than the standard cases.
/// </summary>
public class PayloadReducerSavedGroupTests : UnitTest
{
    [Fact]
    public void ManyConditionGroupsSharingALargeListAreReducedAndKeepTheList()
    {
        const int groupCount = 300;
        var values = new JArray(Enumerable.Range(0, 20_000).Select(i => $"user-{i}"));
        var savedGroups = new JObject { ["ids"] = new JObject { ["type"] = "list", ["attributeKey"] = "id", ["values"] = values } };
        var rules = new JArray();

        for (var i = 0; i < groupCount; i++)
        {
            savedGroups[$"g{i}"] = new JObject
            {
                ["type"] = "condition",
                ["condition"] = new JObject
                {
                    ["country"] = "US",
                    ["$savedGroup"] = new JObject { ["id"] = "ids" },
                    ["plan"] = $"plan-{i}"
                }
            };
            rules.Add(new JObject { ["condition"] = new JObject { ["$savedGroup"] = new JObject { ["id"] = $"g{i}" } }, ["force"] = i });
        }

        var payload = new JObject
        {
            ["features"] = new JObject { ["f"] = new JObject { ["defaultValue"] = -1, ["rules"] = rules } },
            ["savedGroups"] = savedGroups
        };

        var result = PayloadReducer.ReducePayload(payload, JObject.Parse(@"{ ""country"": ""US"" }"));
        var groups = (JObject)result["savedGroups"];

        groups.Properties().Should().HaveCount(groupCount + 1);
        ((JArray)groups["ids"]["values"]).Should().HaveCount(20_000);

        for (var i = 0; i < groupCount; i++)
        {
            // country is known and passes, so only the list reference and plan remain
            JToken.DeepEquals(groups[$"g{i}"]["condition"], JObject.Parse($@"{{ ""$savedGroup"": {{ ""id"": ""ids"" }}, ""plan"": ""plan-{i}"" }}"))
                .Should().BeTrue($"g{i} is reduced against the known attributes");
        }
    }

    [Fact]
    public void GroupsOnACycleKeepTheirConditionAsWritten()
    {
        var payload = JObject.Parse(@"{
            ""features"": {
                ""f"": { ""defaultValue"": false, ""rules"": [
                    { ""condition"": { ""$savedGroup"": { ""id"": ""entry"" } }, ""force"": true },
                    { ""condition"": { ""$savedGroup"": { ""id"": ""self"" } }, ""force"": true }
                ] }
            },
            ""savedGroups"": {
                ""entry"": { ""type"": ""condition"", ""condition"": { ""country"": ""US"", ""$savedGroup"": { ""id"": ""a"" } } },
                ""a"": { ""type"": ""condition"", ""condition"": { ""country"": ""US"", ""$or"": [ { ""$savedGroup"": { ""id"": ""b"" } }, { ""x"": true } ] } },
                ""b"": { ""type"": ""condition"", ""condition"": { ""country"": ""US"", ""z"": true, ""$not"": { ""$savedGroup"": { ""id"": ""a"" } } } },
                ""self"": { ""type"": ""condition"", ""condition"": { ""country"": ""US"", ""$or"": [ { ""$savedGroup"": { ""id"": ""self"" } }, { ""y"": true } ] } }
            }
        }");
        var original = (JObject)payload["savedGroups"];

        // x and z are unknown, so the rule's reference to entry can't be decided and the groups stay
        var groups = (JObject)PayloadReducer.ReducePayload(payload, JObject.Parse(@"{ ""country"": ""US"" }"))["savedGroups"];

        // a and b reference each other, and self references itself
        foreach (var id in new[] { "a", "b", "self" })
        {
            JToken.DeepEquals(groups[id], original[id]).Should().BeTrue($"{id} is on a cycle");
        }

        // entry only leads into the cycle, so it is still reduced
        JToken.DeepEquals(groups["entry"]["condition"], JObject.Parse(@"{ ""$savedGroup"": { ""id"": ""a"" } }")).Should().BeTrue();
    }

    [Fact]
    public void ALongChainOfGroupsDoesNotOverflowTheStack()
    {
        const int length = 50_000;
        var savedGroups = new JObject();

        for (var i = 0; i < length; i++)
        {
            var condition = new JObject { ["country"] = "US" };

            if (i + 1 < length)
            {
                condition["$savedGroup"] = new JObject { ["id"] = $"g{i + 1}" };
            }

            savedGroups[$"g{i}"] = new JObject { ["type"] = "condition", ["condition"] = condition };
        }

        // Referenced inside $elemMatch, so the whole chain keeps its conditions as written
        var payload = new JObject
        {
            ["features"] = JObject.Parse(@"{ ""f"": { ""defaultValue"": false, ""rules"": [
                { ""condition"": { ""items"": { ""$elemMatch"": { ""$savedGroup"": { ""id"": ""g0"" } } } }, ""force"": true }
            ] } }"),
            ["savedGroups"] = savedGroups
        };

        var groups = (JObject)PayloadReducer.ReducePayload(payload, JObject.Parse(@"{ ""country"": ""US"" }"))["savedGroups"];

        groups.Properties().Should().HaveCount(length);
        JToken.DeepEquals(groups[$"g{length - 1}"], savedGroups[$"g{length - 1}"]).Should().BeTrue();
    }
}
