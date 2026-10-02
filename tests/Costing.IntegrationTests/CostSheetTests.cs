using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostSheetTests
{
    [Fact]
    public void CostComponents_CalculateTheirSum_AndOnlyChangesAdvanceVersion()
    {
        var created = CostSheet.Create(new CostId(Guid.NewGuid()), 80m, 20m);
        Assert.True(created.IsSuccess);
        var sheet = created.Value;
        Assert.Equal(100m, CostSheet.Calculate(80m, 20m));
        Assert.True(sheet.UpdateCost(80m, 20m).IsSuccess);
        Assert.Equal(1, sheet.Version);
        Assert.True(sheet.UpdateCost(90m, 20m).IsSuccess);
        Assert.Equal(2, sheet.Version);
        Assert.True(sheet.ApplyCalculation(2, 110m).IsSuccess);
        Assert.Equal(3, sheet.Version);
        Assert.True(sheet.ApplyCalculation(2, 110m).IsSuccess);
        Assert.Equal(3, sheet.Version);
        Assert.False(sheet.ApplyCalculation(1, 100m).IsSuccess);
        Assert.Equal(110m, sheet.UnitCost);
    }
}
