using System;

namespace GrowthBook.Api
{
    public interface IFeatureRefreshSource
    {
        IDisposable SubscribeToRefresh(Action<FeatureRefresh> handler);
    }
}
