using QuantDesk.Modules;
using Xunit;

namespace QuantDesk.UnitTests;

public sealed class ModuleCatalogTests
{
    [Fact]
    public void InitialRoadmapContainsThirteenFunctionalModules()
    {
        Assert.Equal(13, ModuleCatalog.All.Count);
        Assert.Equal(8, ModuleCatalog.All.Count(m => m.Owner == "D1"));
        Assert.Equal(5, ModuleCatalog.All.Count(m => m.Owner == "D2"));
    }
}