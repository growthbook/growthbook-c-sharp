using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Api
{
    public static class HttpHeaders
    {
        public static class ServerSentEvents
        {
            public const string Key = "x-sse-support";
            public const string EnabledValue = "enabled";
        }
    }
}
