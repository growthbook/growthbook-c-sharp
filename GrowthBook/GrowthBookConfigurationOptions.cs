using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    public class GrowthBookConfigurationOptions
    {
        public string ApiHost { get; set; } = "https://cdn.growthbook.io";
        public int CacheExpirationInSeconds { get; set; } = 60;
        public string ClientKey { get; set; }
        public string DecryptionKey { get; set; }
        public bool PreferServerSentEvents { get; set; }
    }
}
