using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Infrastructure;

namespace RetailCloud.Workers;

/// <summary>
/// Daily upkeep (D05 §6, D10 §5.13, INV-001): movement partitions three months ahead, then every consistency oracle.
/// A finding is logged per tenant and counted (rc_oracle_findings_total) for the alert; it is never "fixed" silently.
/// </summary>
public sealed partial class NightlyService(StockMaintenance stock, OracleRunner oracles, FiscalYearRollover fiscal, RetailCloud.Platform.Infrastructure.RetentionJob retention, ILogger<NightlyService> logger)
    : BackgroundService
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
                FiscalYearRollover.Result years = await fiscal.RunAsync(stoppingToken);
                LogPeriods(logger, years.Created);
                foreach ((Guid tenant, string error) in years.Failed)
                {
                    LogPeriodsFailed(logger, tenant, error);
                }

                int expired = await retention.RunAsync(stoppingToken);
                LogRetention(logger, expired);

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

    [LoggerMessage(EventId = 5204, Level = LogLevel.Information, Message = "fiscal periods created: {Created}")]
    private static partial void LogPeriods(ILogger logger, int created);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Critical, Message = "fiscal periods of tenant {TenantId} could not be created: {Error}")]
    private static partial void LogPeriodsFailed(ILogger logger, Guid tenantId, string error);

    [LoggerMessage(EventId = 5206, Level = LogLevel.Information, Message = "retention: {Deleted} rows deleted past their retention")]
    private static partial void LogRetention(ILogger logger, int deleted);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Error, Message = "nightly checks failed")]
    private static partial void LogFailed(ILogger logger, Exception e);
}
