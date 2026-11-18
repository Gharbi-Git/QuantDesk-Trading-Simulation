using QuantDesk.Modules;
using Xunit;

namespace QuantDesk.ArchitectureTests;

public sealed class ModuleDescriptorTests
{
    [Fact]
    public void ModuleCodesAreUnique() =>
        Assert.Equal(ModuleCatalog.All.Count, ModuleCatalog.All.Select(m => m.Code).Distinct().Count());
}