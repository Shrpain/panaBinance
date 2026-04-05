using System;
using System.Collections.Generic;
using System.Linq;
using Binace.Trader.Domain.Indicators;
using Binace.Trader.Domain.Models;

namespace Binace.Trader.Domain.Services;

public class BacktestResult
{
    public decimal TotalProfit { get; set; }
    public decimal TotalFees { get; set; }
    public decimal NetProfit => TotalProfit - TotalFees;
    public int WinCount { get; set; }
    public int LossCount { get; set; }
    public decimal WinRate => (WinCount + LossCount) == 0 ? 0 : (decimal)WinCount / (WinCount + LossCount);
    public List<BacktestTrade> Trades { get; set; } = new();
}

public class BacktestTrade
{
    public string Symbol { get; set; } = "";
    public PositionSide Side { get; set; }
    public DateTime EntryTime { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal Margin { get; set; }
    public int Leverage { get; set; }
    public DateTime? Tp1Time { get; set; }
    public decimal? Tp1Price { get; set; }
    public DateTime CloseTime { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Profit { get; set; }
    public decimal Fees { get; set; }
    public string ExitReason { get; set; } = "";
}

public class BacktestEngine
{
    private const decimal TakerFeeRate = 0.0005m; // 0.05%

    public BacktestResult Run(string symbol, List<MarketKline> klines, decimal leverage, decimal margin, string strategyName)
    {
        var result = new BacktestResult();
        
        if (klines.Count < 100) return result;

        double[] closes = klines.Select(k => (double)k.Close).ToArray();
        double[] highs = klines.Select(k => (double)k.High).ToArray();
        double[] lows = klines.Select(k => (double)k.Low).ToArray();

        // 1. Calculate Indicators & Signals
        bool[] buys;
        bool[] sells;
        double[] trailStops = new double[klines.Count];
        var atrVals = MathEngine.Atr(highs, lows, closes, 100);
        bool useUtLogic = strategyName.Contains("UT Bot", StringComparison.OrdinalIgnoreCase);

        if (useUtLogic)
        {
            var (utBuys, utSells, utTrailStop) = MathEngine.CalcUtBotSignals(highs, lows, closes, 1, 1);
            buys = utBuys;
            sells = utSells;
            trailStops = utTrailStop;
        }
        else if (strategyName.Contains("HalfTrend Only", StringComparison.OrdinalIgnoreCase))
        {
            var (atrHigh, atrLow, htTrends) = MathEngine.CalcHalfTrend(highs, lows, closes, 2, 2);
            buys = new bool[klines.Count];
            sells = new bool[klines.Count];
            for (int j = 1; j < klines.Count; j++)
            {
                buys[j] = htTrends[j] > 0 && htTrends[j - 1] < 0;
                sells[j] = htTrends[j] < 0 && htTrends[j - 1] > 0;
                trailStops[j] = htTrends[j] > 0 ? atrLow[j] : atrHigh[j];
            }
        }
        else if (strategyName.Contains("Range Filter", StringComparison.OrdinalIgnoreCase))
        {
            var (filt, rfBuys, rfSells) = MathEngine.CalcRangeFilter(closes, 100, 3.0);
            buys = rfBuys;
            sells = rfSells;
            for (int j = 0; j < klines.Count; j++)
            {
                trailStops[j] = filt[j];
            }
        }
        else if (strategyName.Contains("SuperTrend", StringComparison.OrdinalIgnoreCase))
        {
            var ema200 = MathEngine.Ema(closes, 200);
            var (st, dir, stBuys, stSells) = MathEngine.CalcSuperTrend(highs, lows, closes, 10, 3);
            buys = new bool[klines.Count];
            sells = new bool[klines.Count];
            for (int j = 0; j < klines.Count; j++)
            {
                buys[j] = stBuys[j] && closes[j] > ema200[j];
                sells[j] = stSells[j] && closes[j] < ema200[j];
                trailStops[j] = st[j];
            }
        }
        else if (strategyName.Contains("Bollinger Bands + Vol", StringComparison.OrdinalIgnoreCase))
        {
            double[] volumes = klines.Select(k => (double)k.Volume).ToArray();
            var (upper, lower, basis, vSpikes) = MathEngine.CalcBollingerBands(closes, volumes, 20, 2.0, 1.5);
            var (bbBuys, bbSells) = MathEngine.CalcBBVolSignals(closes, upper, lower, vSpikes);
            buys = bbBuys;
            sells = bbSells;
            for (int j = 0; j < klines.Count; j++)
            {
                trailStops[j] = buys[j] ? lower[j] : (sells[j] ? upper[j] : basis[j]);
            }
        }
        else
        {
            // Legacy Confluence Strategy
            var hmaVals = MathEngine.CalcHull(closes, 55);
            var (macdLine, signalLine) = MathEngine.CalcMacd(closes, 12, 26, 9);
            var (diPlus, diMinus) = MathEngine.CalcAdx(highs, lows, closes, 14);
            var (atrHigh, atrLow, htTrends) = MathEngine.CalcHalfTrend(highs, lows, closes, 2, 2);
            var macdTrades = MathEngine.CalcTradeSignals(diPlus, diMinus, macdLine, signalLine);
            var (optBuys, optSells) = MathEngine.CalcOptimizedSignals(hmaVals, macdTrades, htTrends);
            buys = optBuys;
            sells = optSells;
            // For legacy, we'll use HalfTrend ATR levels as trailing stops
            for (int j = 0; j < klines.Count; j++)
            {
                trailStops[j] = htTrends[j] > 0 ? atrLow[j] : atrHigh[j];
            }
        }

        MathEngine.CleanSignals(buys, sells);

        // 2. Simulation Loop
        PositionState? pos = null;
        BacktestTrade? currentTrade = null;

        for (int i = 100; i < klines.Count; i++)
        {
            var k = klines[i];
            decimal price = k.Close;

            if (pos == null)
            {
                // Check for Entry
                if (buys[i] || sells[i])
                {
                    var side = buys[i] ? PositionSide.Long : PositionSide.Short;
                    decimal atr = (decimal)atrVals[i];
                    decimal atrDistance = atr * 1.5m;
                    decimal sl = (decimal)trailStops[i];
                    decimal tp = side == PositionSide.Long ? price + atrDistance : price - atrDistance;

                    if (strategyName.Contains("Bollinger Bands + Vol", StringComparison.OrdinalIgnoreCase))
                    {
                        tp = side == PositionSide.Long ? price * 1.02m : price * 0.98m;
                        sl = side == PositionSide.Long ? price * 0.99m : price * 1.01m;
                    }

                    pos = new PositionState
                    {
                        Symbol = symbol,
                        Side = side,
                        EntryPrice = price,
                        Quantity = margin * leverage / price,
                        MarginUsed = margin,
                        Leverage = (int)leverage,
                        InitialStopLoss = sl,
                        TakeProfit1 = tp,
                        CurrentTrailingStop = sl,
                    };

                    currentTrade = new BacktestTrade
                    {
                        Symbol = symbol,
                        Side = side,
                        EntryTime = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).DateTime,
                        EntryPrice = price,
                        Margin = margin,
                        Leverage = (int)leverage,
                    };
                    
                    currentTrade.Fees += (pos.Quantity * price) * TakerFeeRate;
                }
            }
            else
            {
                // Check Exit / State
                decimal low = k.Low;
                decimal high = k.High;
                double trailStopLevel = trailStops[i];

                if (pos.Side == PositionSide.Long)
                {
                    // SL / Trailing Check
                    if (low <= pos.CurrentTrailingStop)
                    {
                        CloseTrade(result, currentTrade, pos, pos.CurrentTrailingStop, "STOP", k);
                        pos = null; currentTrade = null; continue;
                    }

                    // TP1 Check
                    if (!pos.Tp1Hit && high >= pos.TakeProfit1)
                    {
                        pos.Tp1Hit = true;
                        currentTrade!.Tp1Time = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).DateTime;
                        currentTrade.Tp1Price = pos.TakeProfit1;
                        
                        decimal partialVal = (pos.Quantity * 0.5m) * pos.TakeProfit1;
                        currentTrade.Fees += partialVal * TakerFeeRate;
                        currentTrade.Profit += (pos.TakeProfit1 - pos.EntryPrice) * (pos.Quantity * 0.5m);
                        
                        pos.Quantity *= 0.5m;
                        pos.CurrentTrailingStop = Math.Max(pos.CurrentTrailingStop, pos.EntryPrice);
                    }

                    // Trailing Logic
                    if ((double)trailStopLevel > (double)pos.CurrentTrailingStop && (double)trailStopLevel < (double)price)
                    {
                        pos.CurrentTrailingStop = (decimal)trailStopLevel;
                    }
                }
                else
                {
                    // SHORT
                    if (high >= pos.CurrentTrailingStop)
                    {
                        CloseTrade(result, currentTrade, pos, pos.CurrentTrailingStop, "STOP", k);
                        pos = null; currentTrade = null; continue;
                    }

                    if (!pos.Tp1Hit && low <= pos.TakeProfit1)
                    {
                        pos.Tp1Hit = true;
                        currentTrade!.Tp1Time = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).DateTime;
                        currentTrade.Tp1Price = pos.TakeProfit1;
                        
                        decimal partialVal = (pos.Quantity * 0.5m) * pos.TakeProfit1;
                        currentTrade.Fees += partialVal * TakerFeeRate;
                        currentTrade.Profit += (pos.EntryPrice - pos.TakeProfit1) * (pos.Quantity * 0.5m);
                        
                        pos.Quantity *= 0.5m;
                        pos.CurrentTrailingStop = Math.Min(pos.CurrentTrailingStop, pos.EntryPrice);
                    }

                    if (trailStopLevel > 0 && trailStopLevel < (double)pos.CurrentTrailingStop && trailStopLevel > (double)price)
                    {
                        pos.CurrentTrailingStop = (decimal)trailStopLevel;
                    }
                }
            }
        }

        return result;
    }

    private void CloseTrade(BacktestResult result, BacktestTrade? trade, PositionState pos, decimal exitPrice, string reason, MarketKline k)
    {
        if (trade == null) return;

        trade.CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).DateTime;
        trade.ClosePrice = exitPrice;
        trade.ExitReason = reason;

        // Profit for remaining Qty
        decimal move = pos.Side == PositionSide.Long ? (exitPrice - pos.EntryPrice) : (pos.EntryPrice - exitPrice);
        trade.Profit += move * pos.Quantity;
        
        // Fee for final close
        trade.Fees += (pos.Quantity * exitPrice) * TakerFeeRate;

        if (trade.Profit > 0) result.WinCount++;
        else result.LossCount++;

        result.TotalProfit += trade.Profit;
        result.TotalFees += trade.Fees;
        result.Trades.Add(trade);
    }
}
