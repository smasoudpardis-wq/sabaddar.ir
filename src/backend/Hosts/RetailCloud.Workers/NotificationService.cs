using RetailCloud.Notifications.Infrastructure;

namespace RetailCloud.Workers;

/// <summary>US-151: runs the notification dispatcher every few seconds (more often while there is work).</summary>
public sealed partial class NotificationService(NotificationDispatcher dispatcher, ILogger<NotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int handled = await dispatcher.RunOnceAsync(stoppingToken);
                await Task.Delay(handled > 0 ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LogFailed(logger, e);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    [LoggerMessage(EventId = 5103, Level = LogLevel.Error, Message = "notification dispatcher round failed")]
    private static partial void LogFailed(ILogger logger, Exception e);
}
