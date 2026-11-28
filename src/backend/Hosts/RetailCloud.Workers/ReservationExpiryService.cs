using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Infrastructure;

namespace RetailCloud.Workers;

/// <summary>
/// Every minute, in every store: reservations whose 15 minutes have passed give their stock back (INV-004.1, D-19), and carts
/// idle for a day without payments in force are cancelled (SAL-011 [ASM 24h]).
/// </summary>
public sealed partial class ReservationExpiryService(StockMaintenance stock, IUnitOfWork unitOfWork, ILogger<ReservationExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int expired = await stock.ExpireReservationsAsync(stoppingToken);
                if (expired > 0)
                {
                    LogExpired(logger, expired);
                }

                int idle = await unitOfWork.ExecuteWithoutTenantAsync(null, async (s, ct) =>
                {
                    await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT sales.expire_idle_carts()");
                    return (int)(await cmd.ExecuteScalarAsync(ct))!;
                }, stoppingToken);
                if (idle > 0)
                {
                    LogIdleCarts(logger, idle);
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

    [LoggerMessage(EventId = 5211, Level = LogLevel.Information, Message = "stock reservations expired: {Expired}")]
    private static partial void LogExpired(ILogger logger, int expired);

    [LoggerMessage(EventId = 5213, Level = LogLevel.Information, Message = "idle carts cancelled: {Cancelled}")]
    private static partial void LogIdleCarts(ILogger logger, int cancelled);

    [LoggerMessage(EventId = 5212, Level = LogLevel.Error, Message = "expiring stock reservations or idle carts failed")]
    private static partial void LogFailed(ILogger logger, Exception error);
}
