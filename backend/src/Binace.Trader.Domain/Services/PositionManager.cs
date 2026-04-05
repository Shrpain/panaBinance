using System;
using Binace.Trader.Domain.Models;

namespace Binace.Trader.Domain.Services;

public class PositionManager
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PositionState> _positions = [];

    public Func<string, PositionSide, decimal, bool, string, CancellationToken, Task<OrderExecutionOutcome>>? ExecuteMarketOrderAsync { get; set; }

    public PositionState? GetPosition(string symbol)
    {
        lock (_gate)
        {
            return _positions.TryGetValue(symbol, out var position) ? position : null;
        }
    }

    public IReadOnlyList<PositionState> GetActivePositions()
    {
        lock (_gate)
        {
            return _positions.Values.Where(position => position.IsActive).ToArray();
        }
    }

    public async Task<OpenPositionResult?> OpenPositionAsync(string symbol, PositionSide side, decimal entryPrice, decimal quantity, decimal currentAtr, decimal marginUsed, int leverage, decimal? explicitSl = null, decimal? explicitTp = null, CancellationToken cancellationToken = default)
    {
        PositionState position;
        lock (_gate)
        {
            if (_positions.TryGetValue(symbol, out var existing) && existing.IsActive)
            {
                return null;
            }

            decimal tp1 = explicitTp ?? (side == PositionSide.Long ? decimal.MaxValue : 0m);
            decimal sl = explicitSl ?? (side == PositionSide.Long ? 0m : decimal.MaxValue);

            position = new PositionState
            {
                Symbol = symbol,
                Side = side,
                EntryPrice = entryPrice,
                InitialQuantity = quantity,
                Quantity = quantity,
                MarginUsed = marginUsed,
                Leverage = leverage,
                InitialStopLoss = sl,
                TakeProfit1 = tp1,
                CurrentTrailingStop = sl,
                Tp1Hit = false
            };

        }

        Console.WriteLine($"[POSITION OPENED] {side} {symbol} @ {entryPrice} | TP1: {position.TakeProfit1} | SL: {position.InitialStopLoss} | QTY: {quantity}");
        if (ExecuteMarketOrderAsync is not null)
        {
            var execution = await ExecuteMarketOrderAsync(symbol, side, quantity, false, "ENTRY", cancellationToken);
            position.EntryPrice = execution.AveragePrice > 0 ? execution.AveragePrice : entryPrice;
            position.TotalFees += execution.QuoteValue * 0.0005m;
        }

        lock (_gate)
        {
            _positions[symbol] = position;
        }

        return new OpenPositionResult(position.TakeProfit1, position.InitialStopLoss);
    }

    public async Task<PositionCloseSummary?> CheckStateAsync(string symbol, MarketKline currentKline, decimal halfTrendAtrLevel, CancellationToken cancellationToken)
    {
        PositionState? pos;
        lock (_gate)
        {
            if (!_positions.TryGetValue(symbol, out pos) || !pos.IsActive)
            {
                return null;
            }
        }

        decimal currentPrice = currentKline.Close;

        if (halfTrendAtrLevel > 0)
        {
            if (pos.Side == PositionSide.Long && halfTrendAtrLevel < currentPrice && halfTrendAtrLevel > pos.CurrentTrailingStop)
            {
                pos.CurrentTrailingStop = halfTrendAtrLevel;
            }
            else if (pos.Side == PositionSide.Short && halfTrendAtrLevel > currentPrice && halfTrendAtrLevel < pos.CurrentTrailingStop)
            {
                pos.CurrentTrailingStop = halfTrendAtrLevel;
            }
        }

        if (pos.Side == PositionSide.Long)
        {
            if (currentPrice <= pos.CurrentTrailingStop && pos.CurrentTrailingStop > 0m)
            {
                return await ClosePositionAsync(symbol, "STOP_LOSS_OR_TRAILING", cancellationToken);
            }

            if (!pos.Tp1Hit && pos.TakeProfit1 < decimal.MaxValue && currentPrice >= pos.TakeProfit1)
            {
                decimal closeQty = pos.Quantity * 0.5m;
                var nextTrailingStop = Math.Max(pos.CurrentTrailingStop, pos.EntryPrice);

                Console.WriteLine($"[TP1 HIT] Closing 50% ({closeQty}) at {currentPrice}. Moving SL to Break-Even: {nextTrailingStop}");
                if (ExecuteMarketOrderAsync is not null)
                {
                    var execution = await ExecuteMarketOrderAsync(pos.Symbol, PositionSide.Short, closeQty, true, "TAKE_PROFIT_1", cancellationToken);
                    var closePrice = execution.AveragePrice > 0 ? execution.AveragePrice : currentPrice;
                    pos.RealizedPnl += (closePrice - pos.EntryPrice) * closeQty;
                    pos.TotalFees += execution.QuoteValue * 0.0005m;
                }

                pos.Tp1Hit = true;
                pos.Quantity -= closeQty;
                pos.CurrentTrailingStop = nextTrailingStop;
            }
        }
        else if (pos.Side == PositionSide.Short)
        {
            if (currentPrice >= pos.CurrentTrailingStop && pos.CurrentTrailingStop < decimal.MaxValue)
            {
                return await ClosePositionAsync(symbol, "STOP_LOSS_OR_TRAILING", cancellationToken);
            }

            if (!pos.Tp1Hit && pos.TakeProfit1 > 0m && currentPrice <= pos.TakeProfit1)
            {
                decimal closeQty = pos.Quantity * 0.5m;
                var nextTrailingStop = Math.Min(pos.CurrentTrailingStop, pos.EntryPrice);

                Console.WriteLine($"[TP1 HIT] Closing 50% ({closeQty}) at {currentPrice}. Moving SL to Break-Even: {nextTrailingStop}");
                if (ExecuteMarketOrderAsync is not null)
                {
                    var execution = await ExecuteMarketOrderAsync(pos.Symbol, PositionSide.Long, closeQty, true, "TAKE_PROFIT_1", cancellationToken);
                    var closePrice = execution.AveragePrice > 0 ? execution.AveragePrice : currentPrice;
                    pos.RealizedPnl += (pos.EntryPrice - closePrice) * closeQty;
                    pos.TotalFees += execution.QuoteValue * 0.0005m;
                }

                pos.Tp1Hit = true;
                pos.Quantity -= closeQty;
                pos.CurrentTrailingStop = nextTrailingStop;
            }
        }

        return null;
    }

    public async Task<PositionCloseSummary?> ClosePositionAsync(string symbol, string reason, CancellationToken cancellationToken)
    {
        PositionState? position;
        lock (_gate)
        {
            if (!_positions.TryGetValue(symbol, out position) || !position.IsActive)
            {
                return null;
            }
        }

        var sideToClose = position.Side == PositionSide.Long ? PositionSide.Short : PositionSide.Long;
        Console.WriteLine($"[POSITION CLOSED] Reason: {reason}. Closing {position.Quantity} of {position.Symbol}");

        decimal closePrice = position.EntryPrice;

        if (ExecuteMarketOrderAsync is not null)
        {
            var execution = await ExecuteMarketOrderAsync(position.Symbol, sideToClose, position.Quantity, true, reason, cancellationToken);
            closePrice = execution.AveragePrice > 0 ? execution.AveragePrice : closePrice;
            position.TotalFees += execution.QuoteValue * 0.0005m;
        }

        position.RealizedPnl += position.Side == PositionSide.Long
            ? (closePrice - position.EntryPrice) * position.Quantity
            : (position.EntryPrice - closePrice) * position.Quantity;

        var netPnl = position.RealizedPnl - position.TotalFees;
        var roi = position.MarginUsed <= 0 ? 0m : (netPnl / position.MarginUsed) * 100m;

        lock (_gate)
        {
            _positions.Remove(symbol);
        }

        return new PositionCloseSummary(
            Symbol: position.Symbol,
            ClosedTradeSide: position.Side == PositionSide.Long ? "BUY" : "SELL",
            NetPnl: netPnl,
            RoiPercent: roi,
            Reason: reason);
    }
}

public sealed record OpenPositionResult(
    decimal TakeProfit1,
    decimal StopLoss);

public sealed record PositionCloseSummary(
    string Symbol,
    string ClosedTradeSide,
    decimal NetPnl,
    decimal RoiPercent,
    string Reason);

public sealed record OrderExecutionOutcome(
    string Symbol,
    string Side,
    decimal ExecutedQuantity,
    decimal AveragePrice,
    decimal QuoteValue,
    bool ReduceOnly,
    string OrderId);
