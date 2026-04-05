using System;
using System.Collections.Generic;
using System.Linq;

namespace Binace.Trader.Domain.Indicators
{
    public static class MathEngine
    {
        public static double[] Sma(double[] src, int length)
        {
            var outArr = new double[src.Length];
            for (var i = 0; i < src.Length; i++) outArr[i] = double.NaN;

            for (var i = length - 1; i < src.Length; i++)
            {
                double sum = 0;
                int validCount = 0;
                for (var j = 0; j < length; j++)
                {
                    if (!double.IsNaN(src[i - j]))
                    {
                        sum += src[i - j];
                        validCount++;
                    }
                }
                if (validCount == length) outArr[i] = sum / length;
            }
            return outArr;
        }

        public static double[] Ema(double[] src, int length)
        {
            var outArr = new double[src.Length];
            for (var i = 0; i < src.Length; i++) outArr[i] = double.NaN;

            double alpha = 2.0 / (length + 1);
            double prevEma = double.NaN;

            for (var i = 0; i < src.Length; i++)
            {
                if (double.IsNaN(src[i])) continue;
                
                if (double.IsNaN(prevEma))
                {
                    prevEma = src[i];
                    outArr[i] = prevEma;
                }
                else
                {
                    prevEma = alpha * src[i] + (1 - alpha) * prevEma;
                    outArr[i] = prevEma;
                }
            }
            return outArr;
        }

        public static double[] Rma(double[] src, int length)
        {
            var outArr = new double[src.Length];
            for (var i = 0; i < src.Length; i++) outArr[i] = double.NaN;

            double alpha = 1.0 / length;
            double prevRma = double.NaN;

            for (var i = 0; i < src.Length; i++)
            {
                if (double.IsNaN(src[i])) continue;

                if (double.IsNaN(prevRma))
                {
                    // Compute initial SMA
                    if (i >= length - 1)
                    {
                        double sum = 0;
                        for (var j = 0; j < length; j++) sum += src[i - j];
                        prevRma = sum / length;
                        outArr[i] = prevRma;
                    }
                }
                else
                {
                    prevRma = alpha * src[i] + (1 - alpha) * prevRma;
                    outArr[i] = prevRma;
                }
            }
            return outArr;
        }

        public static (bool[] Buys, bool[] Sells, double[] TrailStop) CalcUtBotSignals(double[] highs, double[] lows, double[] closes, double keyMultiplier, int atrPeriod)
    {
        int n = closes.Length;
        var buys = new bool[n];
        var sells = new bool[n];
        var trailStop = new double[n];
        
        var atr = Atr(highs, lows, closes, atrPeriod);
        
        double currentTrailStop = 0;
        int pos = 0;

        for (int i = 1; i < n; i++)
        {
            double src = closes[i];
            double prevSrc = closes[i - 1];
            double nLoss = keyMultiplier * atr[i];
            double prevTrailStop = trailStop[i - 1];

            if (src > prevTrailStop && prevSrc > prevTrailStop)
            {
                currentTrailStop = Math.Max(prevTrailStop, src - nLoss);
            }
            else if (src < prevTrailStop && prevSrc < prevTrailStop)
            {
                currentTrailStop = Math.Min(prevTrailStop, src + nLoss);
            }
            else if (src > prevTrailStop)
            {
                currentTrailStop = src - nLoss;
            }
            else
            {
                currentTrailStop = src + nLoss;
            }

            trailStop[i] = currentTrailStop;

            int prevPos = pos;
            if (prevSrc < prevTrailStop && src > currentTrailStop)
            {
                pos = 1;
            }
            else if (prevSrc > prevTrailStop && src < currentTrailStop)
            {
                pos = -1;
            }

            // Buy when above and crossover
            // crossover(ema(src,1), trail) -> src crossover trail
            bool crossover = prevSrc <= prevTrailStop && src > currentTrailStop;
            bool crossunder = prevSrc >= prevTrailStop && src < currentTrailStop;

            buys[i] = src > currentTrailStop && crossover;
            sells[i] = src < currentTrailStop && crossunder;
        }

        return (buys, sells, trailStop);
    }

    public static double[] Wma(double[] src, int length)
        {
            var outArr = new double[src.Length];
            for (var i = 0; i < src.Length; i++) outArr[i] = double.NaN;
            
            double norm = 0;
            for (var j = 1; j <= length; j++) norm += j;

            for (var i = length - 1; i < src.Length; i++)
            {
                double sum = 0;
                for (var j = 0; j < length; j++)
                {
                    double weight = length - j;
                    sum += src[i - j] * weight;
                }
                outArr[i] = sum / norm;
            }
            return outArr;
        }

        public static double[] Atr(double[] highs, double[] lows, double[] closes, int length)
        {
            var tr = new double[highs.Length];
            for (var i = 0; i < highs.Length; i++) tr[i] = double.NaN;

            for (var i = 0; i < highs.Length; i++)
            {
                double prevClose = (i > 0 && !double.IsNaN(closes[i - 1])) ? closes[i - 1] : closes[i];
                double tr1 = highs[i] - lows[i];
                double tr2 = Math.Abs(highs[i] - prevClose);
                double tr3 = Math.Abs(lows[i] - prevClose);
                tr[i] = Math.Max(tr1, Math.Max(tr2, tr3));
            }
            return Rma(tr, length);
        }
        
        public static double[] CalcHull(double[] src, int length)
        {
            var wmaHalf = Wma(src, length / 2);
            var wmaFull = Wma(src, length);
            var diff = new double[src.Length];
            
            for (var i = 0; i < src.Length; i++)
            {
                if (!double.IsNaN(wmaHalf[i]) && !double.IsNaN(wmaFull[i]))
                    diff[i] = 2 * wmaHalf[i] - wmaFull[i];
                else
                    diff[i] = double.NaN;
            }
            int sqn = (int)Math.Round(Math.Sqrt(length));
            return Wma(diff, sqn);
        }

        public static (double[] macdLine, double[] signalLine) CalcMacd(double[] src, int fast, int slow, int signal)
        {
            var fastEma = Ema(src, fast);
            var slowEma = Ema(src, slow);
            var macdLine = new double[src.Length];
            
            for (var i = 0; i < src.Length; i++)
            {
                if (!double.IsNaN(fastEma[i]) && !double.IsNaN(slowEma[i]))
                    macdLine[i] = fastEma[i] - slowEma[i];
                else
                    macdLine[i] = double.NaN;
            }
            var signalLine = Ema(macdLine, signal);
            return (macdLine, signalLine);
        }

        public static (double[] diPlus, double[] diMinus) CalcAdx(double[] highs, double[] lows, double[] closes, int length)
        {
            var tr = new double[highs.Length];
            var plusDm = new double[highs.Length];
            var minusDm = new double[highs.Length];

            for(int i = 0; i < highs.Length; i++) { tr[i] = double.NaN; plusDm[i] = double.NaN; minusDm[i] = double.NaN; }

            for (var i = 1; i < highs.Length; i++)
            {
                double upMove = highs[i] - highs[i - 1];
                double downMove = lows[i - 1] - lows[i];

                double prevClose = closes[i - 1];
                tr[i] = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - prevClose), Math.Abs(lows[i] - prevClose)));
                plusDm[i] = (upMove > downMove && upMove > 0) ? upMove : 0;
                minusDm[i] = (downMove > upMove && downMove > 0) ? downMove : 0;
            }

            var smoothedTr = Rma(tr, length);
            var smoothedPlusDm = Rma(plusDm, length);
            var smoothedMinusDm = Rma(minusDm, length);

            var diPlus = new double[highs.Length];
            var diMinus = new double[highs.Length];

            for (var i = 0; i < highs.Length; i++)
            {
                diPlus[i] = double.IsNaN(smoothedPlusDm[i]) || double.IsNaN(smoothedTr[i]) || smoothedTr[i] == 0 ? 0 : 100 * smoothedPlusDm[i] / smoothedTr[i];
                diMinus[i] = double.IsNaN(smoothedMinusDm[i]) || double.IsNaN(smoothedTr[i]) || smoothedTr[i] == 0 ? 0 : 100 * smoothedMinusDm[i] / smoothedTr[i];
            }
            return (diPlus, diMinus);
        }

        public static int[] CalcTradeSignals(double[] diPlus, double[] diMinus, double[] macdLine, double[] signalLine)
        {
            var trades = new int[diPlus.Length];
            int currentTrade = 0;

            for (var i = 0; i < diPlus.Length; i++)
            {
                bool longCheck = diPlus[i] > diMinus[i] && macdLine[i] > signalLine[i];
                bool shortCheck = diMinus[i] > diPlus[i] && signalLine[i] > macdLine[i];

                int nextTrade = currentTrade;

                if (currentTrade == 0 && longCheck) nextTrade = 1;
                else if (currentTrade == 0 && shortCheck) nextTrade = -1;
                else if (currentTrade == 1 && shortCheck) nextTrade = -1;
                else if (currentTrade == -1 && longCheck) nextTrade = 1;

                trades[i] = nextTrade;
                currentTrade = nextTrade;
            }
            return trades;
        }

        public static (double[] up, double[] down, int[] trends) CalcHalfTrend(double[] highs, double[] lows, double[] closes, int amplitude = 2, int channelDeviation = 2)
        {
            var atrVals = Atr(highs, lows, closes, 100);
            var highma = Sma(highs, amplitude);
            var lowma = Sma(lows, amplitude);

            var trends = new int[highs.Length];
            var atrHigh = new double[highs.Length];
            var atrLow = new double[highs.Length];
            var upLine = new double[highs.Length];
            var downLine = new double[highs.Length];

            int trend = 0;
            int nextTrend = 0;
            double maxLowPrice = lows[0];
            double minHighPrice = highs[0];
            double up = 0.0;
            double down = 0.0;

            for (var i = 0; i < highs.Length; i++)
            {
                if (double.IsNaN(highs[i])) continue;

                double highPrice = highs[i];
                double lowPrice = lows[i];
                for (var j = 0; j < amplitude && i - j >= 0; j++)
                {
                    if (!double.IsNaN(highs[i - j])) highPrice = Math.Max(highPrice, highs[i - j]);
                    if (!double.IsNaN(lows[i - j])) lowPrice = Math.Min(lowPrice, lows[i - j]);
                }

                double prevLow = i > 0 ? lows[i - 1] : lows[i];
                double prevHigh = i > 0 ? highs[i - 1] : highs[i];
                int prevTrend = trend;

                if (nextTrend == 1)
                {
                    maxLowPrice = Math.Max(lowPrice, maxLowPrice);
                    if (highma[i] < maxLowPrice && closes[i] < prevLow)
                    {
                        trend = 1;
                        nextTrend = 0;
                        minHighPrice = highPrice;
                    }
                }
                else
                {
                    minHighPrice = Math.Min(highPrice, minHighPrice);
                    if (lowma[i] > minHighPrice && closes[i] > prevHigh)
                    {
                        trend = 0;
                        nextTrend = 1;
                        maxLowPrice = lowPrice;
                    }
                }

                double atr2 = atrVals[i] / 2;
                double dev = channelDeviation * atr2;

                if (trend == 0)
                {
                    if (prevTrend != 0) up = down;
                    else up = Math.Max(maxLowPrice, up);
                    
                    atrHigh[i] = up + dev;
                    atrLow[i] = up - dev;
                }
                else
                {
                    if (prevTrend != 1) down = up;
                    else down = Math.Min(minHighPrice, down);
                    
                    atrHigh[i] = down + dev;
                    atrLow[i] = down - dev;
                }

                upLine[i] = up;
                downLine[i] = down;
                trends[i] = trend;
            }
            return (atrHigh, atrLow, trends);
        }

        public static (bool[] buy, bool[] sell) CalcOptimizedSignals(double[] hma, int[] macdTrades, int[] halfTrendStates)
        {
            var buys = new bool[hma.Length];
            var sells = new bool[hma.Length];
            int currentOptState = 0;

            for (var i = 2; i < hma.Length; i++)
            {
                if (double.IsNaN(hma[i])) continue;

                int hullState = hma[i] > hma[i - 2] ? 1 : -1;
                int macdState = macdTrades[i];
                int htState = halfTrendStates[i] == 0 ? 1 : -1;

                int score = hullState + macdState + htState;

                if (currentOptState != 1 && score >= 2)
                {
                    currentOptState = 1;
                    buys[i] = true;
                }
                else if (currentOptState != -1 && score <= -2)
                {
                    currentOptState = -1;
                    sells[i] = true;
                }
            }
            return (buys, sells);
        }

        public static double[] Stdev(double[] src, int length)
        {
            var outArr = new double[src.Length];
            var sma = Sma(src, length);
            for (var i = 0; i < src.Length; i++) outArr[i] = double.NaN;

            for (var i = length - 1; i < src.Length; i++)
            {
                if (double.IsNaN(sma[i])) continue;
                double sumSqrDiff = 0;
                for (var j = 0; j < length; j++)
                {
                    double diff = src[i - j] - sma[i];
                    sumSqrDiff += diff * diff;
                }
                outArr[i] = Math.Sqrt(sumSqrDiff / length);
            }
            return outArr;
        }

        public static (double[] Upper, double[] Lower, double[] Basis, bool[] VolumeSpikes) CalcBollingerBands(double[] closes, double[] volumes, int bbLength, double bbStdDev, double volMultiplier)
        {
            var basis = Sma(closes, bbLength);
            var dev = Stdev(closes, bbLength);
            var upper = new double[closes.Length];
            var lower = new double[closes.Length];
            
            for (int i = 0; i < closes.Length; i++)
            {
                if (double.IsNaN(basis[i]) || double.IsNaN(dev[i]))
                {
                    upper[i] = double.NaN;
                    lower[i] = double.NaN;
                }
                else
                {
                    upper[i] = basis[i] + bbStdDev * dev[i];
                    lower[i] = basis[i] - bbStdDev * dev[i];
                }
            }

            var avgVol = Sma(volumes, 20);
            var volumeSpikes = new bool[closes.Length];
            for (int i = 0; i < closes.Length; i++)
            {
                volumeSpikes[i] = !double.IsNaN(avgVol[i]) && volumes[i] > avgVol[i] * volMultiplier;
            }

            return (upper, lower, basis, volumeSpikes);
        }

        public static (bool[] Buys, bool[] Sells) CalcBBVolSignals(double[] closes, double[] upper, double[] lower, bool[] volumeSpikes)
        {
            var buys = new bool[closes.Length];
            var sells = new bool[closes.Length];
            for (int i = 0; i < closes.Length; i++)
            {
                buys[i] = !double.IsNaN(upper[i]) && closes[i] > upper[i] && volumeSpikes[i];
                sells[i] = !double.IsNaN(lower[i]) && closes[i] < lower[i] && volumeSpikes[i];
            }
            return (buys, sells);
        }

        public static (double[] SuperTrend, int[] Direction, bool[] Buys, bool[] Sells) CalcSuperTrend(double[] highs, double[] lows, double[] closes, int period = 10, double multiplier = 3)
        {
            int n = closes.Length;
            var atr = Atr(highs, lows, closes, period);
            var upperBand = new double[n];
            var lowerBand = new double[n];
            var superTrend = new double[n];
            var direction = new int[n];
            var buys = new bool[n];
            var sells = new bool[n];

            for (int i = 0; i < n; i++)
            {
                superTrend[i] = double.NaN;
                direction[i] = 1;
            }

            double src0 = (highs[0] + lows[0]) / 2;
            double atr0 = double.IsNaN(atr[0]) ? 0 : atr[0];
            upperBand[0] = src0 + multiplier * atr0;
            lowerBand[0] = src0 - multiplier * atr0;

            for (int i = 1; i < n; i++)
            {
                double src = (highs[i] + lows[i]) / 2;
                double currentAtr = double.IsNaN(atr[i]) ? 0 : atr[i];
                double basicUpperBand = src + multiplier * currentAtr;
                double basicLowerBand = src - multiplier * currentAtr;

                // Final Upper Band
                if (basicUpperBand < upperBand[i - 1] || closes[i - 1] > upperBand[i - 1])
                    upperBand[i] = basicUpperBand;
                else
                    upperBand[i] = upperBand[i - 1];

                // Final Lower Band
                if (basicLowerBand > lowerBand[i - 1] || closes[i - 1] < lowerBand[i - 1])
                    lowerBand[i] = basicLowerBand;
                else
                    lowerBand[i] = lowerBand[i - 1];

                int prevDir = direction[i - 1];
                int currentDir = prevDir;

                if (prevDir == 1 && closes[i] < lowerBand[i])
                    currentDir = -1;
                else if (prevDir == -1 && closes[i] > upperBand[i])
                    currentDir = 1;

                direction[i] = currentDir;
                superTrend[i] = currentDir == 1 ? lowerBand[i] : upperBand[i];

                if (currentDir == 1 && prevDir == -1) buys[i] = true;
                else if (currentDir == -1 && prevDir == 1) sells[i] = true;
            }

            return (superTrend, direction, buys, sells);
        }

        public static (double[] Filter, bool[] Buys, bool[] Sells) CalcRangeFilter(double[] src, int period = 100, double multiplier = 3.0)
        {
            int n = src.Length;
            var filter = new double[n];
            var buys = new bool[n];
            var sells = new bool[n];
            
            // 1. Smooth Average Range
            // avrng = ta.ema(math.abs(x - x[1]), t)
            // smoothrng = ta.ema(avrng, t * 2 - 1) * m
            var absDiff = new double[n];
            for (int i = 1; i < n; i++) absDiff[i] = Math.Abs(src[i] - src[i - 1]);
            var avrng = Ema(absDiff, period);
            var smrng = Ema(avrng, period * 2 - 1);
            for (int i = 0; i < n; i++) smrng[i] *= multiplier;

            // 2. Range Filter
            filter[0] = src[0];
            for (int i = 1; i < n; i++)
            {
                double r = smrng[i];
                double prevFilt = filter[i - 1];
                if (src[i] > prevFilt)
                {
                    filter[i] = (src[i] - r < prevFilt) ? prevFilt : src[i] - r;
                }
                else
                {
                    filter[i] = (src[i] + r > prevFilt) ? prevFilt : src[i] + r;
                }
            }

            // 3. Direction & Signals
            var upward = new double[n];
            var downward = new double[n];
            var condIni = new int[n]; // 1 for long, -1 for short

            for (int i = 1; i < n; i++)
            {
                upward[i] = filter[i] > filter[i - 1] ? upward[i - 1] + 1 : (filter[i] < filter[i - 1] ? 0 : upward[i - 1]);
                downward[i] = filter[i] < filter[i - 1] ? downward[i - 1] + 1 : (filter[i] > filter[i - 1] ? 0 : downward[i - 1]);

                bool longCond = (src[i] > filter[i] && src[i] > src[i - 1] && upward[i] > 0) ||
                                (src[i] > filter[i] && src[i] < src[i - 1] && upward[i] > 0);
                bool shortCond = (src[i] < filter[i] && src[i] < src[i - 1] && downward[i] > 0) ||
                                 (src[i] < filter[i] && src[i] > src[i - 1] && downward[i] > 0);

                int prevCondIni = condIni[i - 1];
                condIni[i] = longCond ? 1 : (shortCond ? -1 : prevCondIni);
                
                buys[i] = longCond && prevCondIni == -1;
                sells[i] = shortCond && prevCondIni == 1;
            }

            return (filter, buys, sells);
        }

        public static void CleanSignals(bool[] buys, bool[] sells)
        {
            int lastSignal = 0; // 0: none, 1: buy, -1: sell
            for (int i = 0; i < buys.Length; i++)
            {
                if (buys[i] && sells[i])
                {
                    // Contradictory signals at same index? Kill both to be safe.
                    buys[i] = false;
                    sells[i] = false;
                    continue;
                }

                if (buys[i])
                {
                    if (lastSignal == 1) buys[i] = false;
                    else lastSignal = 1;
                }
                else if (sells[i])
                {
                    if (lastSignal == -1) sells[i] = false;
                    else lastSignal = -1;
                }
            }
        }
    }
}
