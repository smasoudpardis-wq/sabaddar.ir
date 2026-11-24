using RetailCloud.Inventory.Infrastructure;

namespace RetailCloud.Workers;

/// <summary>INV-004.1 / D-19: every minute, reservations whose 15 minutes have passed give their stock back in every store.</summary>
public sealed partial class ReservationExpiryService(StockMaintenance stock, ILogger<ReservationExpiryService> logger) : BackgroundService
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

    [LoggerMessage(EventId = 5212, Level = LogLevel.Error, Message = "expiring stock reservations failed")]
    private static partial void LogFailed(ILogger logger, Exception error);
}
