using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Infrastructure;

namespace RetailCloud.Workers;

/// <summary>
/// Daily upkeep (D05 §6, D10 §5.13, INV-001): movement partitions three months ahead, then every consistency oracle.
/// A finding is logged per tenant and counted (rc_oracle_findings_total) for the alert; it is never "fixed" silently.
/// </summary>
public sealed partial class NightlyService(StockMaintenance stock, OracleRunner oracles, ILogger<NightlyService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int created = await stock.EnsurePartitionsAsync(3, stoppingToken);
                LogPartitions(logger, created);
                foreach (OracleFinding f in await oracles.RunAsync(stoppingToken))
                {
                    LogFinding(logger, f.Oracle, f.TenantId, f.Detail);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LogFailed(logger, e);
            }

            await Task.Delay(Every, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "stock movement partitions created: {Created}")]
    private static partial void LogPartitions(ILogger logger, int created);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Critical, Message = "oracle {Oracle} found an inconsistency in tenant {TenantId}: {Detail}")]
    private static partial void LogFinding(ILogger logger, string oracle, Guid tenantId, string detail);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Error, Message = "nightly checks failed")]
    private static partial void LogFailed(ILogger logger, Exception e);
}
