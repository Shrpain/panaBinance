using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Binace.Trader.Application.Contracts;
using Binace.Trader.Domain.Models;
using Binace.Trader.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Binace.Trader.Infrastructure.Services;

public sealed class BinanceApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BinanceApiClient> _logger;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly Lock _exchangeInfoGate = new();
    private Dictionary<string, FuturesSymbolRules>? _futuresSymbolRulesCache;

    private const string SpotBaseUrl = "https://api.binance.com";
    private const string FuturesBaseUrl = "https://fapi.binance.com";

    public bool HasCredentials => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret);

    public BinanceApiClient(
        IHttpClientFactory httpClientFactory,
        ILogger<BinanceApiClient> logger,
        string apiKey,
        string apiSecret)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _apiKey = apiKey;
        _apiSecret = apiSecret;
    }

    // ──────────── Spot ────────────

    public async Task<IReadOnlyList<AccountBalanceDto>> GetSpotBalancesAsync(CancellationToken ct)
    {
        var json = await SignedGetAsync(SpotBaseUrl, "/api/v3/account", "omitZeroBalances=true", ct);
        var balances = new List<AccountBalanceDto>();

        if (json.TryGetProperty("balances", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                var free = ParseDecimal(item, "free");
                var locked = ParseDecimal(item, "locked");
                if (free > 0 || locked > 0)
                {
                    balances.Add(new AccountBalanceDto(
                        Asset: item.GetProperty("asset").GetString()!,
                        Market: "Spot",
                        Free: free,
                        Locked: locked,
                        WalletBalance: free + locked,
                        UnrealizedPnl: 0m));
                }
            }
        }

        return balances;
    }

    public async Task<IReadOnlyList<TradeHistoryDto>> GetSpotTradesAsync(string symbol, int limit, CancellationToken ct)
    {
        var json = await SignedGetAsync(SpotBaseUrl, "/api/v3/myTrades", $"symbol={symbol}&limit={limit}", ct);
        var trades = new List<TradeHistoryDto>();

        foreach (var item in json.EnumerateArray())
        {
            var isBuyer = item.TryGetProperty("isBuyer", out var buyerVal) && buyerVal.GetBoolean();
            trades.Add(new TradeHistoryDto(
                Id: item.GetProperty("id").GetInt64(),
                Symbol: symbol,
                Side: isBuyer ? "BUY" : "SELL",
                Price: ParseDecimal(item, "price"),
                Quantity: ParseDecimal(item, "qty"),
                QuoteQuantity: ParseDecimal(item, "quoteQty"),
                Commission: ParseDecimal(item, "commission"),
                CommissionAsset: item.GetProperty("commissionAsset").GetString() ?? "",
                RealizedPnl: 0m,
                Time: DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("time").GetInt64())));
        }

        return trades;
    }

    public async Task<IReadOnlyList<TickerPriceDto>> GetSpotTickersAsync(CancellationToken ct)
    {
        var json = await PublicGetAsync(SpotBaseUrl, "/api/v3/ticker/24hr", "", ct);
        var tickers = new List<TickerPriceDto>();

        foreach (var item in json.EnumerateArray())
        {
            var symbol = item.GetProperty("symbol").GetString() ?? "";
            if (!symbol.EndsWith("USDT")) continue;

            tickers.Add(new TickerPriceDto(
                Symbol: symbol,
                Price: ParseDecimal(item, "lastPrice"),
                PriceChangePercent24h: ParseDecimal(item, "priceChangePercent"),
                Volume24h: ParseDecimal(item, "quoteVolume")));
        }

        return tickers.OrderByDescending(t => t.Volume24h).ToList();
    }

    // ──────────── Futures ────────────

    public async Task<IReadOnlyList<AccountBalanceDto>> GetFuturesBalancesAsync(CancellationToken ct)
    {
        var json = await SignedGetAsync(FuturesBaseUrl, "/fapi/v2/balance", "", ct);
        var balances = new List<AccountBalanceDto>();

        foreach (var item in json.EnumerateArray())
        {
            var walletBalance = ParseDecimal(item, "balance");
            var available = ParseDecimal(item, "availableBalance");
            var crossUnPnl = ParseDecimal(item, "crossUnPnl");

            if (walletBalance > 0 || available > 0)
            {
                balances.Add(new AccountBalanceDto(
                    Asset: item.GetProperty("asset").GetString()!,
                    Market: "Futures",
                    Free: available,
                    Locked: walletBalance - available,
                    WalletBalance: walletBalance,
                    UnrealizedPnl: crossUnPnl));
            }
        }

        return balances;
    }

    public async Task<IReadOnlyList<OpenPositionDto>> GetFuturesPositionsAsync(CancellationToken ct)
    {
        var json = await SignedGetAsync(FuturesBaseUrl, "/fapi/v2/positionRisk", "", ct);
        var positions = new List<OpenPositionDto>();

        foreach (var item in json.EnumerateArray())
        {
            var positionAmt = ParseDecimal(item, "positionAmt");
            if (positionAmt == 0) continue;

            var entryPrice = ParseDecimal(item, "entryPrice");
            var markPrice = ParseDecimal(item, "markPrice");
            var unrealizedProfit = ParseDecimal(item, "unRealizedProfit");

            positions.Add(new OpenPositionDto(
                Symbol: item.GetProperty("symbol").GetString()!,
                Side: positionAmt > 0 ? "Long" : "Short",
                EntryPrice: entryPrice,
                MarkPrice: markPrice,
                Quantity: Math.Abs(positionAmt),
                Pnl: unrealizedProfit));
        }

        return positions;
    }

    public async Task<IReadOnlyList<TradeHistoryDto>> GetFuturesTradesAsync(string symbol, int limit, CancellationToken ct)
    {
        var json = await SignedGetAsync(FuturesBaseUrl, "/fapi/v1/userTrades", $"symbol={symbol}&limit={limit}", ct);
        var trades = new List<TradeHistoryDto>();

        foreach (var item in json.EnumerateArray())
        {
            var isBuyer = item.TryGetProperty("buyer", out var buyerVal) && buyerVal.GetBoolean();
            trades.Add(new TradeHistoryDto(
                Id: item.GetProperty("id").GetInt64(),
                Symbol: symbol,
                Side: isBuyer ? "BUY" : "SELL",
                Price: ParseDecimal(item, "price"),
                Quantity: ParseDecimal(item, "qty"),
                QuoteQuantity: ParseDecimal(item, "quoteQty"),
                Commission: ParseDecimal(item, "commission"),
                CommissionAsset: item.GetProperty("commissionAsset").GetString() ?? "",
                RealizedPnl: ParseDecimal(item, "realizedPnl"),
                Time: DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("time").GetInt64())));
        }

        return trades;
    }

    public async Task<IReadOnlyList<TickerPriceDto>> GetFuturesTickersAsync(CancellationToken ct)
    {
        var json = await PublicGetAsync(FuturesBaseUrl, "/fapi/v1/ticker/24hr", "", ct);
        var tickers = new List<TickerPriceDto>();

        foreach (var item in json.EnumerateArray())
        {
            var symbol = item.GetProperty("symbol").GetString() ?? "";
            if (!symbol.EndsWith("USDT")) continue;

            tickers.Add(new TickerPriceDto(
                Symbol: symbol,
                Price: ParseDecimal(item, "lastPrice"),
                PriceChangePercent24h: ParseDecimal(item, "priceChangePercent"),
                Volume24h: ParseDecimal(item, "quoteVolume")));
        }

        return tickers.OrderByDescending(t => t.Volume24h).ToList();
    }

    public async Task<IReadOnlyList<MarketKline>> GetFuturesKlinesAsync(string symbol, string interval, int limit, CancellationToken ct)
    {
        var json = await PublicGetAsync(FuturesBaseUrl, "/fapi/v1/klines", $"symbol={symbol}&interval={interval}&limit={limit}", ct);
        var klines = new List<MarketKline>();

        foreach (var item in json.EnumerateArray())
        {
            klines.Add(new MarketKline
            {
                OpenTime = item[0].GetInt64(),
                Open = ParseDecimalArrayElem(item, 1),
                High = ParseDecimalArrayElem(item, 2),
                Low = ParseDecimalArrayElem(item, 3),
                Close = ParseDecimalArrayElem(item, 4),
                Volume = ParseDecimalArrayElem(item, 5),
                CloseTime = item[6].GetInt64()
            });
        }

        return klines;
    }

    public async Task SetFuturesLeverageAsync(string symbol, int leverage, CancellationToken ct)
    {
        await SignedPostAsync(FuturesBaseUrl, "/fapi/v1/leverage", $"symbol={symbol}&leverage={leverage}", ct);
    }

    public async Task<decimal> NormalizeFuturesOrderQuantityAsync(string symbol, decimal rawQuantity, decimal referencePrice, CancellationToken ct)
    {
        var rules = await GetFuturesSymbolRulesAsync(symbol, ct);
        var quantity = FloorToStep(rawQuantity, rules.StepSize);

        if (quantity < rules.MinQty)
        {
            throw new InvalidOperationException($"Calculated quantity {quantity} is below Binance minimum quantity {rules.MinQty} for {symbol}.");
        }

        var notional = quantity * referencePrice;
        if (rules.MinNotional > 0 && notional < rules.MinNotional)
        {
            throw new InvalidOperationException($"Calculated notional {notional} is below Binance minimum notional {rules.MinNotional} for {symbol}.");
        }

        return quantity;
    }

    public async Task<OrderExecutionOutcome> PlaceFuturesMarketOrderAsync(
        string symbol,
        PositionSide side,
        decimal quantity,
        bool reduceOnly,
        CancellationToken ct)
    {
        var sideValue = side == PositionSide.Long ? "BUY" : "SELL";
        var reduceOnlyValue = reduceOnly ? "&reduceOnly=true" : string.Empty;
        var query =
            $"symbol={symbol}&side={sideValue}&type=MARKET&quantity={quantity.ToString(CultureInfo.InvariantCulture)}{reduceOnlyValue}&newOrderRespType=RESULT";

        var json = await SignedPostAsync(FuturesBaseUrl, "/fapi/v1/order", query, ct);
        var avgPrice = ParseDecimal(json, "avgPrice");
        var executedQuantity = ParseDecimal(json, "executedQty");
        var quoteValue = ParseDecimal(json, "cumQuote");
        var orderId = json.TryGetProperty("orderId", out var orderIdValue) ? orderIdValue.ToString() : string.Empty;

        return new OrderExecutionOutcome(
            Symbol: symbol,
            Side: sideValue,
            ExecutedQuantity: executedQuantity == 0 ? quantity : executedQuantity,
            AveragePrice: avgPrice,
            QuoteValue: quoteValue,
            ReduceOnly: reduceOnly,
            OrderId: orderId);
    }


    public async Task<decimal> GetFuturesTotalBalanceAsync(CancellationToken ct)
    {
        var balances = await GetFuturesBalancesAsync(ct);
        return balances.Where(b => b.Asset == "USDT").Select(b => b.WalletBalance).FirstOrDefault();
    }

    public async Task<decimal> GetFuturesTotalUnrealizedPnlAsync(CancellationToken ct)
    {
        var positions = await GetFuturesPositionsAsync(ct);
        return positions.Sum(p => p.Pnl);
    }

    // ──────────── HTTP helpers ────────────

    private async Task<JsonElement> SignedGetAsync(string baseUrl, string path, string queryParams, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var baseQuery = string.IsNullOrEmpty(queryParams)
            ? $"recvWindow=5000&timestamp={timestamp}"
            : $"{queryParams}&recvWindow=5000&timestamp={timestamp}";
        var signature = Sign(baseQuery);
        var url = $"{baseUrl}{path}?{baseQuery}&signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-MBX-APIKEY", _apiKey);

        var client = _httpClientFactory.CreateClient("external");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Binance API error {Path}: {Status} {Body}", path, (int)response.StatusCode, body[..Math.Min(body.Length, 300)]);
            throw new HttpRequestException($"Binance {path} returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> SignedPostAsync(string baseUrl, string path, string queryParams, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var baseQuery = string.IsNullOrEmpty(queryParams)
            ? $"recvWindow=5000&timestamp={timestamp}"
            : $"{queryParams}&recvWindow=5000&timestamp={timestamp}";
        var signature = Sign(baseQuery);
        var url = $"{baseUrl}{path}?{baseQuery}&signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-MBX-APIKEY", _apiKey);

        var client = _httpClientFactory.CreateClient("external");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Binance API POST error {Path}: {Status} {Body}", path, (int)response.StatusCode, body[..Math.Min(body.Length, 300)]);
            throw new HttpRequestException($"Binance {path} returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> PublicGetAsync(string baseUrl, string path, string queryParams, CancellationToken ct)
    {
        var url = string.IsNullOrEmpty(queryParams)
            ? $"{baseUrl}{path}"
            : $"{baseUrl}{path}?{queryParams}";

        var client = _httpClientFactory.CreateClient("external");
        using var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Binance public API error {Path}: {Status}", path, (int)response.StatusCode);
            throw new HttpRequestException($"Binance {path} returned {(int)response.StatusCode}");
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static decimal ParseDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0m;
        var raw = value.GetString();
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0m;
    }

    private static decimal ParseDecimalArrayElem(JsonElement arrayElement, int index)
    {
        var value = arrayElement[index];
        if (value.ValueKind == JsonValueKind.String)
        {
            return decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0m;
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDecimal();
        }
        return 0m;
    }

    private async Task<FuturesSymbolRules> GetFuturesSymbolRulesAsync(string symbol, CancellationToken ct)
    {
        lock (_exchangeInfoGate)
        {
            if (_futuresSymbolRulesCache is not null && _futuresSymbolRulesCache.TryGetValue(symbol, out var cached))
            {
                return cached;
            }
        }

        var json = await PublicGetAsync(FuturesBaseUrl, "/fapi/v1/exchangeInfo", "", ct);
        var rules = new Dictionary<string, FuturesSymbolRules>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in json.GetProperty("symbols").EnumerateArray())
        {
            var currentSymbol = item.GetProperty("symbol").GetString() ?? string.Empty;
            decimal stepSize = 0.001m;
            decimal minQty = 0m;
            decimal minNotional = 0m;

            foreach (var filter in item.GetProperty("filters").EnumerateArray())
            {
                var filterType = filter.GetProperty("filterType").GetString();
                if (string.Equals(filterType, "MARKET_LOT_SIZE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(filterType, "LOT_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    stepSize = ParseDecimal(filter, "stepSize");
                    minQty = ParseDecimal(filter, "minQty");
                }

                if (string.Equals(filterType, "MIN_NOTIONAL", StringComparison.OrdinalIgnoreCase))
                {
                    minNotional = ParseDecimal(filter, "notional");
                }
            }

            rules[currentSymbol] = new FuturesSymbolRules(
                MinQty: minQty,
                StepSize: stepSize == 0 ? 0.001m : stepSize,
                MinNotional: minNotional);
        }

        lock (_exchangeInfoGate)
        {
            _futuresSymbolRulesCache = rules;
            return _futuresSymbolRulesCache.TryGetValue(symbol, out var parsed)
                ? parsed
                : new FuturesSymbolRules(0.001m, 0.001m, 0m);
        }
    }

    private static decimal FloorToStep(decimal value, decimal stepSize)
    {
        if (stepSize <= 0)
        {
            return value;
        }

        var steps = Math.Floor(value / stepSize);
        return steps * stepSize;
    }

    private sealed record FuturesSymbolRules(decimal MinQty, decimal StepSize, decimal MinNotional);
}
