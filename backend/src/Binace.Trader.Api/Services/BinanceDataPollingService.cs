using Binace.Trader.Application.Abstractions;
using Binace.Trader.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Binace.Trader.Api.Services;

public sealed class BinanceDataPollingService : BackgroundService
{
    private readonly ITradingDashboardService _dashboard;
    private readonly IHubContext<BotUpdatesHub> _hubContext;
    private readonly ILogger<BinanceDataPollingService> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public BinanceDataPollingService(
        ITradingDashboardService dashboard,
        IHubContext<BotUpdatesHub> hubContext,
        ILogger<BinanceDataPollingService> logger)
    {
        _dashboard = dashboard;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BinanceDataPollingService started — pushing updates every {Interval}s", PollInterval.TotalSeconds);

        // Give the app time to fully start
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshotTask = _dashboard.GetSnapshotAsync(stoppingToken);
                var positionsTask = _dashboard.GetOpenPositionsAsync(stoppingToken);
                var tickersTask = _dashboard.GetTickerPricesAsync(stoppingToken);

                await Task.WhenAll(snapshotTask, positionsTask, tickersTask);

                await _hubContext.Clients.All.SendAsync("dashboardUpdated", await snapshotTask, stoppingToken);
                await _hubContext.Clients.All.SendAsync("positionsUpdated", await positionsTask, stoppingToken);
                await _hubContext.Clients.All.SendAsync("tickersUpdated", await tickersTask, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error during Binance data polling cycle");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("BinanceDataPollingService stopped");
    }
}
