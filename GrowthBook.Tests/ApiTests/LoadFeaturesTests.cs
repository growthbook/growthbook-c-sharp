using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class LoadFeaturesTests
{
    private const string ApiKey = "<AN API KEY GOES HERE>";

    [Fact(Skip = "This is here as a shortcut for the purpose of debugging this SDK against the actual API and should not be used otherwise")]
    public async Task LoadFromApi()
    {
        var context = new Context
        {
            ClientKey = ApiKey,
            DefaultLogLevel = LogLevel.Debug
        };

        var gb = new GrowthBook(context);
        await gb.LoadFeatures();

        // This is effectively an API test harness and so it should wait infinitely
        // so the test doesn't exit mid-debug session.

        await Task.Delay(-1);
    }
}
