namespace QuantDesk.Modules;

// Documentation/bootstrapping metadata. These descriptors do NOT mean modules are implemented.
public sealed record ModuleDescriptor(string Code, string Name, string Owner);
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDescriptor> All { get; } = new[]
    {
        new ModuleDescriptor("M01", "MarketData", "D1"),
        new ModuleDescriptor("M02", "Indicators", "D1"),
        new ModuleDescriptor("M03", "StrategyRegistry", "D1"),
        new ModuleDescriptor("M04", "Backtesting", "D1"),
        new ModuleDescriptor("M05", "PaperTrading", "D1"),
        new ModuleDescriptor("M06", "Matching", "D2"),
        new ModuleDescriptor("M07", "Portfolio", "D2"),
        new ModuleDescriptor("M08", "Risk", "D1"),
        new ModuleDescriptor("M09", "Analytics", "D1"),
        new ModuleDescriptor("M11", "Options", "D1"),
        new ModuleDescriptor("M12", "MarketSimulation", "D2"),
        new ModuleDescriptor("M13", "Arena", "D2"),
        new ModuleDescriptor("M14", "Administration", "D2")
    };
}