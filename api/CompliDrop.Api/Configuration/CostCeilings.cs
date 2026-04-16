namespace CompliDrop.Api.Configuration;

public class CostCeilings
{
    public decimal FreeTierMonthlyUsd { get; set; } = 5m;
    public decimal PaidTierMonthlyUsd { get; set; } = 50m;
}
