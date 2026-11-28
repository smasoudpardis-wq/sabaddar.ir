using RetailCloud.Inventory.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-064 / INV-008: the inbound side of a transfer carries exactly what the outbound side took, line by line.</summary>
public sealed class TransferRulesTests
{
    [Fact]
    public void Each_inbound_line_brings_the_value_its_outbound_line_took_to_the_destination()
    {
        Guid from = Guid.CreateVersion7(), to = Guid.CreateVersion7(), a = Guid.CreateVersion7(), b = Guid.CreateVersion7();
        StockLine[] outbound = [new(1, from, a, MovementType.TransferOut, -3), new(2, from, b, MovementType.TransferOut, -2.5m)];
        PostedMovement[] issued =
        [
            new(1, from, a, MovementType.TransferOut, -3, 60_000, -180_000, 7, 60_000),
            new(2, from, b, MovementType.TransferOut, -2.5m, 33_333, -83_333, 0, 33_333),
        ];
        IReadOnlyList<StockLine> inbound = TransferRules.Inbound(to, outbound, issued);
        Assert.Equal([new StockLine(1, to, a, MovementType.TransferIn, 3, 180_000), new StockLine(2, to, b, MovementType.TransferIn, 2.5m, 83_333)], inbound);
        Assert.Equal(issued.Sum(m => -m.TotalCost), inbound.Sum(l => l.Cost));   // nothing gained or lost in the move
    }
}
