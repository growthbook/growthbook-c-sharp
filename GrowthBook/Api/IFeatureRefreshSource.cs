using System;
using System.Collections.Generic;

namespace GrowthBook.Api
{
    public interface IFeatureRefreshSource
    {
        IDisposable SubscribeToRefresh(Action<IDictionary<string, Feature>> handler);
    }
}
