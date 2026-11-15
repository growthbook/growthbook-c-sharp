using System.Linq;
using FluentAssertions;
using GrowthBook.Api.SSE;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class SSEEventParserTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AppendData_CommentBeforeData_PreservesFeatureUpdate(string newline)
    {
        const string data = "{\"features\":{\"checkout\":{\"defaultValue\":true}}}";
        var parser = new SSEEventParser();

        var events = parser.AppendData($": heartbeat{newline}data: {data}{newline}{newline}").ToList();

        events.Should().ContainSingle();
        events[0].Data.Should().Be(data);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AppendData_CommentsOnly_DoesNotEmitEvent(string newline)
    {
        var parser = new SSEEventParser();

        var events = parser.AppendData($": heartbeat{newline}: keepalive{newline}{newline}");

        events.Should().BeEmpty();
    }
}
