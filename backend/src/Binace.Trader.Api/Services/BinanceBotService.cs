using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binace.Trader.Api.Hubs;
using Binace.Trader.Application.Abstractions;
using Binace.Trader.Application.Contracts;
using Binace.Trader.Domain.Indicators;
using Binace.Trader.Domain.Models;
using Binace.Trader.Domain.Services;
using Binace.Trader.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;

namespace Binace.Trader.Api.Services;

public class BinanceBotService : BackgroundService
{
    private readonly BinanceApiClient _apiClient;
    private readonly IBotRuntimeService _botRuntimeService;
    private readonly IBotTerminalEventService _botTerminalEventService;
    private readonly IHubContext<BotUpdatesHub> _hubContext;
    private readonly PositionManager _positionManager;
    private readonly INotificationService _notificationService;
    private readonly ILogger<BinanceBotService> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, DateTimeOffset> _signalReceivedAt = [];

    public BinanceBotService(
        BinanceApiClient apiClient,
        IBotRuntimeService botRuntimeService,
        IBotTerminalEventService botTerminalEventService,
        IHubContext<BotUpdatesHub> hubContext,
        PositionManager positionManager,
        INotificationService notificationService,
        ILogger<BinanceBotService> logger)
    {
        _apiClient = apiClient;
        _botRuntimeService = botRuntimeService;
        _botTerminalEventService = botTerminalEventService;
        _hubContext = hubContext;
        _positionManager = positionManager;
        _notificationService = notificationService;
        _logger = logger;

        _positionManager.ExecuteMarketOrderAsync = ExecuteOrderAsync;
    }

    private async Task<OrderExecutionOutcome> ExecuteOrderAsync(string symbol, PositionSide side, decimal qty, bool reduceOnly, string reason, CancellationToken cancellationToken)
    {
        _logger.LogWarning(">>> [LIVE BOT EXECUTION] Sending {Side} Order for {Qty} {Symbol}. Reason: {Reason}. ReduceOnly={ReduceOnly} <<<", side, qty, symbol, reason, reduceOnly);
        var execution = await _apiClient.PlaceFuturesMarketOrderAsync(symbol, side, qty, reduceOnly, cancellationToken);

        if (string.Equals(reason, "ENTRY", StringComparison.OrdinalIgnoreCase))
        {
            await PublishEventAsync("success", $"Vào lệnh {execution.Side} thành công: {symbol}", cancellationToken);

            if (_signalReceivedAt.TryGetValue(symbol, out var signalTime))
            {
                var latencySeconds = Math.Max(0m, (decimal)(DateTimeOffset.UtcNow - signalTime).TotalSeconds);
                await PublishEventAsync("info", $"lệch (s): {latencySeconds:F2}", cancellationToken);
                _signalReceivedAt.Remove(symbol);
            }

            // Notification
            var msg = $"✅ **đã mở lệnh: {execution.Side}**\n" +
                      $"🔹 coin: {symbol}\n" +
                      $"🔹 sl: --\n" + 
                      $"🔹 tp: --\n" +
                      $"🔹 pnl: 0.00\n" +
                      $"🔹 Roi: 0.00%\n"; 
            await _notificationService.SendNotificationAsync(msg, cancellationToken);
        }

        return execution;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BinanceBotService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_apiClient.HasCredentials)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                var runtime = _botRuntimeService.GetState();
                var activeSymbols = _positionManager.GetActivePositions().Select(position => position.Symbol);
                var symbolsToProcess = runtime.TraderSymbols
                    .Concat(activeSymbols)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (symbolsToProcess.Length == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                foreach (var symbol in symbolsToProcess)
                {
                    await ProcessSymbolAsync(symbol, runtime, runtime.IsRunning, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bot cycle failed.");
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
    }

    private async Task ProcessSymbolAsync(string symbol, BotRuntimeStateDto runtime, bool allowNewEntries, CancellationToken cancellationToken)
    {
        var klines = await _apiClient.GetFuturesKlinesAsync(symbol, runtime.Interval, 500, cancellationToken);
        if (klines.Count < 100)
        {
            return;
        }

        double[] closes = klines.Select(k => (double)k.Close).ToArray();
        double[] highs = klines.Select(k => (double)k.High).ToArray();
        double[] lows = klines.Select(k => (double)k.Low).ToArray();

        int lastIdx = klines.Count - 1;
        bool isBuy = false;
        bool isSell = false;
        double trailStopLevelVal = 0;
        decimal? explicitTp = null;
        decimal? explicitSl = null;

        bool[] buys = new bool[closes.Length];
        bool[] sells = new bool[closes.Length];

        if (string.Equals(runtime.Strategy, "UT Bot Alerts", StringComparison.OrdinalIgnoreCase))
        {
            var (utBuys, utSells, utTrailStop) = MathEngine.CalcUtBotSignals(highs, lows, closes, 1, 1);
            buys = utBuys; sells = utSells;
            trailStopLevelVal = utTrailStop[lastIdx];
        }
        else if (string.Equals(runtime.Strategy, "Confluence Engine", StringComparison.OrdinalIgnoreCase))
        {
            var hmaVals = MathEngine.CalcHull(closes, 55);
            var (macdLine, signalLine) = MathEngine.CalcMacd(closes, 12, 26, 9);
            var (diPlus, diMinus) = MathEngine.CalcAdx(highs, lows, closes, 14);
            var (atrHigh, atrLow, htTrends) = MathEngine.CalcHalfTrend(highs, lows, closes, 2, 2);
            var macdTrades = MathEngine.CalcTradeSignals(diPlus, diMinus, macdLine, signalLine);
            var (optBuys, optSells) = MathEngine.CalcOptimizedSignals(hmaVals, macdTrades, htTrends);
            buys = optBuys; sells = optSells;
            trailStopLevelVal = htTrends[lastIdx] > 0 ? atrLow[lastIdx] : atrHigh[lastIdx];
        }
        else if (string.Equals(runtime.Strategy, "Range Filter", StringComparison.OrdinalIgnoreCase))
        {
            var (filt, rfBuys, rfSells) = MathEngine.CalcRangeFilter(closes, 100, 3.0);
            buys = rfBuys; sells = rfSells;
            trailStopLevelVal = filt[lastIdx];
        }
        else if (string.Equals(runtime.Strategy, "SuperTrend + EMA 200", StringComparison.OrdinalIgnoreCase))
        {
            var ema200 = MathEngine.Ema(closes, 200);
            var (st, dir, stBuys, stSells) = MathEngine.CalcSuperTrend(highs, lows, closes, 10, 3);
            for (int i = 0; i < closes.Length; i++)
            {
                buys[i] = stBuys[i] && closes[i] > (double)ema200[i];
                sells[i] = stSells[i] && closes[i] < (double)ema200[i];
            }
            trailStopLevelVal = st[lastIdx];
        }
        else if (string.Equals(runtime.Strategy, "Bollinger Bands + Vol", StringComparison.OrdinalIgnoreCase))
        {
            double[] volumes = klines.Select(k => (double)k.Volume).ToArray();
            var (upper, lower, basis, vSpikes) = MathEngine.CalcBollingerBands(closes, volumes, 20, 2.0, 1.5);
            var (bbBuys, bbSells) = MathEngine.CalcBBVolSignals(closes, upper, lower, vSpikes);
            buys = bbBuys; sells = bbSells;
            trailStopLevelVal = isBuy ? lower[lastIdx] : (isSell ? upper[lastIdx] : basis[lastIdx]);
        }
        else
        {
            var (atrHigh, atrLow, htTrends) = MathEngine.CalcHalfTrend(highs, lows, closes, 2, 2);
            for (int i = 1; i < klines.Count; i++)
            {
                buys[i] = htTrends[i] > 0 && htTrends[i - 1] < 0;
                sells[i] = htTrends[i] < 0 && htTrends[i - 1] > 0;
            }
            trailStopLevelVal = htTrends[lastIdx] > 0 ? atrLow[lastIdx] : atrHigh[lastIdx];
        }

        MathEngine.CleanSignals(buys, sells);
        isBuy = buys[lastIdx];
        isSell = sells[lastIdx];

        if (string.Equals(runtime.Strategy, "Bollinger Bands + Vol", StringComparison.OrdinalIgnoreCase))
        {
            if (isBuy)
            {
                explicitTp = (decimal)closes[lastIdx] * 1.02m;
                explicitSl = (decimal)closes[lastIdx] * 0.99m;
            }
            else if (isSell)
            {
                explicitTp = (decimal)closes[lastIdx] * 0.98m;
                explicitSl = (decimal)closes[lastIdx] * 1.01m;
            }
        }
        else
        {
            if (!double.IsNaN(trailStopLevelVal))
            {
                explicitSl = (decimal)trailStopLevelVal;
            }
        }

        decimal trailStopLevel = (decimal)trailStopLevelVal;
        decimal currentAtr = (decimal)MathEngine.Atr(highs, lows, closes, 100)[lastIdx];
        var currentKline = klines[lastIdx];
        var currentPosition = _positionManager.GetPosition(symbol);
        var futuresBalances = await _apiClient.GetFuturesBalancesAsync(cancellationToken);
        var availableUsdt = futuresBalances
            .Where(balance => string.Equals(balance.Asset, "USDT", StringComparison.OrdinalIgnoreCase))
            .Select(balance => balance.Free)
            .FirstOrDefault();
        var effectiveMargin = availableUsdt * (runtime.Margin / 100m);

        if (currentPosition == null || !currentPosition.IsActive)
        {
            if (!allowNewEntries)
            {
                return;
            }

            if (!isBuy && !isSell)
            {
                return;
            }

            var side = isBuy ? PositionSide.Long : PositionSide.Short;
            _signalReceivedAt[symbol] = DateTimeOffset.UtcNow;
            await PublishEventAsync("warning", $"đã nhận tín hiệu {(side == PositionSide.Long ? "BUY" : "SELL")}: {symbol}", cancellationToken);
            if (effectiveMargin <= 0)
            {
                _logger.LogWarning("Skipping {Symbol} entry because available USDT margin is 0.", symbol);
                return;
            }

            await _apiClient.SetFuturesLeverageAsync(symbol, runtime.Leverage, cancellationToken);
            var rawQuantity = (effectiveMargin * runtime.Leverage) / currentKline.Close;
            var quantity = await _apiClient.NormalizeFuturesOrderQuantityAsync(symbol, rawQuantity, currentKline.Close, cancellationToken);

            _logger.LogInformation(">>> OPTIMIZED {Side} SIGNAL DETECTED for {Symbol}. Entering position with effectiveMargin={Margin} <<<", side, symbol, effectiveMargin);
            var result = await _positionManager.OpenPositionAsync(symbol, side, currentKline.Close, quantity, currentAtr, effectiveMargin, runtime.Leverage, explicitSl, explicitTp, cancellationToken);
            if (result != null)
            {
                await PublishEventAsync("info", $"SL: {result.StopLoss:F4} | TP: {result.TakeProfit1:F4}", cancellationToken);
            }
        }
        else
        {
            var reverseToLong = currentPosition.Side == PositionSide.Short && isBuy;
            var reverseToShort = currentPosition.Side == PositionSide.Long && isSell;

            if (allowNewEntries && (reverseToLong || reverseToShort))
            {
                var nextSide = reverseToLong ? PositionSide.Long : PositionSide.Short;
                _logger.LogInformation(">>> REVERSAL SIGNAL DETECTED for {Symbol}. Closing {CurrentSide} and opening {NextSide} <<<", symbol, currentPosition.Side, nextSide);
                _signalReceivedAt[symbol] = DateTimeOffset.UtcNow;
                await PublishEventAsync("warning", $"đã nhận tín hiệu {(nextSide == PositionSide.Long ? "BUY" : "SELL")}: {symbol}", cancellationToken);
                var closed = await _positionManager.ClosePositionAsync(symbol, "REVERSAL_SIGNAL", cancellationToken);
                if (closed is not null)
                {
                    await PublishCloseSummaryAsync(closed, cancellationToken);
                }

                var refreshedBalances = await _apiClient.GetFuturesBalancesAsync(cancellationToken);
                var refreshedAvailableUsdt = refreshedBalances
                    .Where(balance => string.Equals(balance.Asset, "USDT", StringComparison.OrdinalIgnoreCase))
                    .Select(balance => balance.Free)
                    .FirstOrDefault();
                var refreshedMargin = refreshedAvailableUsdt * (runtime.Margin / 100m);

                if (refreshedMargin <= 0)
                {
                    _logger.LogWarning("Skipping reversal entry for {Symbol} because available USDT margin is 0 after close.", symbol);
                    await PublishEventAsync("error", $"không thể mở lệnh đảo chiều {nextSide} cho {symbol} vì số dư không đủ", cancellationToken);
                    return;
                }

                await _apiClient.SetFuturesLeverageAsync(symbol, runtime.Leverage, cancellationToken);
                var rawQuantity = (refreshedMargin * runtime.Leverage) / currentKline.Close;
                var quantity = await _apiClient.NormalizeFuturesOrderQuantityAsync(symbol, rawQuantity, currentKline.Close, cancellationToken);
                var result = await _positionManager.OpenPositionAsync(symbol, nextSide, currentKline.Close, quantity, currentAtr, refreshedMargin, runtime.Leverage, explicitSl, explicitTp, cancellationToken);
                if (result != null)
                {
                    await PublishEventAsync("info", $"SL: {result.StopLoss:F4} | TP: {result.TakeProfit1:F4}", cancellationToken);
                }
                return;
            }

            var closeSummary = await _positionManager.CheckStateAsync(symbol, currentKline, trailStopLevel, cancellationToken);
            if (closeSummary is not null)
            {
                await PublishCloseSummaryAsync(closeSummary, cancellationToken);
            }
        }
    }

    private async Task PublishCloseSummaryAsync(PositionCloseSummary closeSummary, CancellationToken cancellationToken)
    {
        var reasonText = closeSummary.Reason switch {
            "STOP_LOSS_OR_TRAILING" => "chạm Stop Loss/Trailing",
            "REVERSAL_SIGNAL" => "đảo chiều (Reverse)",
            "TAKE_PROFIT_1" => "chốt lời 50%",
            _ => closeSummary.Reason
        };
        await PublishEventAsync("success", $"đóng lệnh {closeSummary.ClosedTradeSide} của {closeSummary.Symbol} ({reasonText})", cancellationToken);
        await PublishEventAsync("info", $"lợi nhuận: {closeSummary.NetPnl:F2}", cancellationToken);
        await PublishEventAsync("info", $"ROI: {closeSummary.RoiPercent:F2}%", cancellationToken);

        // Notification
        var msg = $"❌ **đã đóng lệnh: {closeSummary.ClosedTradeSide}**\n" +
                  $"🔹 coin: {closeSummary.Symbol}\n" +
                  $"🔹 sl: --\n" +
                  $"🔹 tp: --\n" +
                  $"🔹 pnl: {closeSummary.NetPnl:F2}\n" +
                  $"🔹 Roi: {closeSummary.RoiPercent:F2}%\n";
        await _notificationService.SendNotificationAsync(msg, cancellationToken);
    }

    private async Task PublishEventAsync(string level, string message, CancellationToken cancellationToken)
    {
        var item = _botTerminalEventService.Add(level, message);
        await _hubContext.Clients.All.SendAsync("botEventReceived", item, cancellationToken);
    }
}
