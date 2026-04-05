using Binace.Trader.Application.Abstractions;
using Binace.Trader.Application.Contracts;
using Binace.Trader.Domain.Enums;
using Binace.Trader.Domain.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Binace.Trader.Infrastructure.Services;

public sealed class InMemoryTradingDashboardService : ITradingDashboardService
{
    private readonly Lock _gate = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InMemoryTradingDashboardService> _logger;
    private readonly IntegrationSettingsDto _integrationSettings;
    private readonly BinanceApiClient _binanceClient;
    private readonly PositionManager _positionManager;

    private BotConfigurationDto _configuration = new(
        Exchange: "Binance",
        Symbol: "BTCUSDT",
        Strategy: "EMA Cross",
        RiskPerTradePercent: 1.0m,
        PaperTradingEnabled: false);

    public InMemoryTradingDashboardService(
        IHostEnvironment hostEnvironment,
        IHttpClientFactory httpClientFactory,
        BinanceApiClient binanceClient,
        PositionManager positionManager,
        ILogger<InMemoryTradingDashboardService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _binanceClient = binanceClient;
        _positionManager = positionManager;
        _logger = logger;
        _integrationSettings = LoadIntegrationSettings(hostEnvironment.ContentRootPath);
    }

    public async Task<DashboardSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_binanceClient.HasCredentials)
        {
            return EmptySnapshot();
        }

        try
        {
            var balanceTask = _binanceClient.GetFuturesTotalBalanceAsync(cancellationToken);
            var pnlTask = _binanceClient.GetFuturesTotalUnrealizedPnlAsync(cancellationToken);
            var positionsTask = _binanceClient.GetFuturesPositionsAsync(cancellationToken);

            await Task.WhenAll(balanceTask, pnlTask, positionsTask);

            var totalBalance = await balanceTask;
            var unrealizedPnl = await pnlTask;
            var positions = await positionsTask;

            return new DashboardSnapshotDto(
                Exchange: _configuration.Exchange,
                Symbol: _configuration.Symbol,
                Strategy: _configuration.Strategy,
                Status: BotRunState.Running.ToString(),
                LastHeartbeatUtc: DateTimeOffset.UtcNow,
                DailyPnl: unrealizedPnl,
                UnrealizedPnl: unrealizedPnl,
                WinRate: 0m,
                OpenPositions: positions.Count,
                EquityCurve: [new MetricPointDto(DateTimeOffset.UtcNow, totalBalance)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch dashboard snapshot from Binance");
            return EmptySnapshot();
        }
    }

    public async Task<IReadOnlyList<OpenPositionDto>> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        if (!_binanceClient.HasCredentials)
        {
            return [];
        }

        try
        {
            var binancePositions = await _binanceClient.GetFuturesPositionsAsync(cancellationToken);
            var enriched = new List<OpenPositionDto>();

            foreach (var pos in binancePositions)
            {
                var internalPos = _positionManager.GetPosition(pos.Symbol);
                if (internalPos != null && internalPos.IsActive)
                {
                    enriched.Add(pos with 
                    { 
                        StopLoss = internalPos.CurrentTrailingStop, 
                        TakeProfit = internalPos.TakeProfit1 
                    });
                }
                else
                {
                    enriched.Add(pos);
                }
            }

            return enriched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch open positions from Binance");
            return [];
        }
    }

    public Task<BotConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_configuration);
        }
    }

    public Task<IntegrationSettingsDto> GetIntegrationSettingsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_integrationSettings);
        }
    }

    public async Task<IReadOnlyList<AccountBalanceDto>> GetAccountBalancesAsync(CancellationToken cancellationToken)
    {
        if (!_binanceClient.HasCredentials)
        {
            return [];
        }

        try
        {
            var spotTask = _binanceClient.GetSpotBalancesAsync(cancellationToken);
            var futuresTask = _binanceClient.GetFuturesBalancesAsync(cancellationToken);
            await Task.WhenAll(spotTask, futuresTask);

            var all = new List<AccountBalanceDto>();
            all.AddRange(await spotTask);
            all.AddRange(await futuresTask);
            return all;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch account balances from Binance");
            return [];
        }
    }

    public async Task<IReadOnlyList<TradeHistoryDto>> GetTradeHistoryAsync(CancellationToken cancellationToken)
    {
        if (!_binanceClient.HasCredentials)
        {
            return [];
        }

        try
        {
            var symbols = new[] { "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT" };
            var tasks = symbols.Select(s => SafeGetFuturesTrades(s, 10, cancellationToken));
            var results = await Task.WhenAll(tasks);
            return results.SelectMany(r => r).OrderByDescending(t => t.Time).Take(50).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch trade history from Binance");
            return [];
        }
    }

    public async Task<IReadOnlyList<TickerPriceDto>> GetTickerPricesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _binanceClient.GetFuturesTickersAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch ticker prices from Binance");
            return [];
        }
    }

    public async Task<ConnectionTestResultDto> TestBinanceConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_integrationSettings.ApiKey) || string.IsNullOrWhiteSpace(_integrationSettings.ApiSecret))
        {
            return CreateFailureResult("Binance", "Missing Binance API credentials in tt.txt.");
        }

        const decimal recvWindow = 5000m;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = $"omitZeroBalances=true&recvWindow={recvWindow.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}";
        var signature = CreateBinanceSignature(query, _integrationSettings.ApiSecret);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.binance.com/api/v3/account?{query}&signature={signature}");
        request.Headers.Add("X-MBX-APIKEY", _integrationSettings.ApiKey);

        var client = _httpClientFactory.CreateClient("external");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return CreateFailureResult(
                "Binance",
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                Summarize(body));
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accountType = root.TryGetProperty("accountType", out var accountTypeValue) ? accountTypeValue.GetString() : "UNKNOWN";
        var canTrade = root.TryGetProperty("canTrade", out var canTradeValue) && canTradeValue.GetBoolean();
        var permissions = root.TryGetProperty("permissions", out var permissionsValue)
            ? string.Join(", ", permissionsValue.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)))
            : "n/a";

        return new ConnectionTestResultDto(
            Service: "Binance",
            Success: true,
            Message: $"Connected to {accountType} account.",
            Details: $"canTrade={canTrade}; permissions={permissions}",
            CheckedAtUtc: DateTimeOffset.UtcNow);
    }

    public async Task<ConnectionTestResultDto> TestTelegramConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_integrationSettings.TelegramBotToken))
        {
            return CreateFailureResult("Telegram", "Missing Telegram bot token in tt.txt.");
        }

        var client = _httpClientFactory.CreateClient("external");
        var encodedToken = Uri.EscapeDataString(_integrationSettings.TelegramBotToken);

        using var getMeResponse = await client.GetAsync($"https://api.telegram.org/bot{encodedToken}/getMe", cancellationToken);
        var getMeBody = await getMeResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!getMeResponse.IsSuccessStatusCode)
        {
            return CreateFailureResult(
                "Telegram",
                $"HTTP {(int)getMeResponse.StatusCode} {getMeResponse.ReasonPhrase}",
                Summarize(getMeBody));
        }

        using var getMeDocument = JsonDocument.Parse(getMeBody);
        var result = getMeDocument.RootElement.GetProperty("result");
        var username = result.TryGetProperty("username", out var usernameValue) ? usernameValue.GetString() : null;
        var firstName = result.TryGetProperty("first_name", out var firstNameValue) ? firstNameValue.GetString() : "Unknown";
        var details = !string.IsNullOrWhiteSpace(username) ? $"bot=@{username}" : $"bot={firstName}";

        if (!string.IsNullOrWhiteSpace(_integrationSettings.WebhookUrl))
        {
            using var webhookResponse = await client.GetAsync($"https://api.telegram.org/bot{encodedToken}/getWebhookInfo", cancellationToken);
            var webhookBody = await webhookResponse.Content.ReadAsStringAsync(cancellationToken);

            if (webhookResponse.IsSuccessStatusCode)
            {
                using var webhookDocument = JsonDocument.Parse(webhookBody);
                var webhookInfo = webhookDocument.RootElement.GetProperty("result");
                var activeWebhookUrl = webhookInfo.TryGetProperty("url", out var urlValue) ? urlValue.GetString() ?? string.Empty : string.Empty;
                var pendingCount = webhookInfo.TryGetProperty("pending_update_count", out var pendingValue) ? pendingValue.GetInt32() : 0;
                var matchesConfiguredWebhook = string.Equals(activeWebhookUrl, _integrationSettings.WebhookUrl, StringComparison.OrdinalIgnoreCase);
                details = $"{details}; webhook={(matchesConfiguredWebhook ? "matched" : "different")}; pending={pendingCount}";
            }
        }

        return new ConnectionTestResultDto(
            Service: "Telegram",
            Success: true,
            Message: "Connected to Telegram Bot API.",
            Details: details,
            CheckedAtUtc: DateTimeOffset.UtcNow);
    }

    public Task<BotConfigurationDto> SaveConfigurationAsync(UpdateBotConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _configuration = new BotConfigurationDto(
                Exchange: _configuration.Exchange,
                Symbol: request.Symbol.Trim().ToUpperInvariant(),
                Strategy: request.Strategy.Trim(),
                RiskPerTradePercent: Math.Clamp(request.RiskPerTradePercent, 0.10m, 5m),
                PaperTradingEnabled: request.PaperTradingEnabled);

            return Task.FromResult(_configuration);
        }
    }

    // ──────────── helpers ────────────

    private async Task<IReadOnlyList<TradeHistoryDto>> SafeGetFuturesTrades(string symbol, int limit, CancellationToken ct)
    {
        try
        {
            return await _binanceClient.GetFuturesTradesAsync(symbol, limit, ct);
        }
        catch
        {
            return [];
        }
    }

    private DashboardSnapshotDto EmptySnapshot() => new(
        Exchange: _configuration.Exchange,
        Symbol: _configuration.Symbol,
        Strategy: _configuration.Strategy,
        Status: BotRunState.Stopped.ToString(),
        LastHeartbeatUtc: DateTimeOffset.UtcNow,
        DailyPnl: 0m,
        UnrealizedPnl: 0m,
        WinRate: 0m,
        OpenPositions: 0,
        EquityCurve: []);

    private static IntegrationSettingsDto LoadIntegrationSettings(string contentRootPath)
    {
        var workspaceFile = FindWorkspaceFile(contentRootPath, "tt.txt");
        if (workspaceFile is null)
        {
            return new IntegrationSettingsDto(
                ApiKey: string.Empty,
                ApiSecret: string.Empty,
                TelegramBotToken: string.Empty,
                AutoDetectChatId: false,
                WebhookUrl: string.Empty,
                ZaloToken: string.Empty,
                AllowedIpAddresses: [],
                LoadedFromFile: false,
                SourcePath: string.Empty);
        }

        var rawLines = File.ReadAllLines(workspaceFile);
        return TtFileParser.Parse(rawLines, workspaceFile);
    }

    private static string? FindWorkspaceFile(string startDirectory, string fileName)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static ConnectionTestResultDto CreateFailureResult(string service, string message, string details = "")
    {
        return new ConnectionTestResultDto(
            Service: service,
            Success: false,
            Message: message,
            Details: details,
            CheckedAtUtc: DateTimeOffset.UtcNow);
    }

    private static string CreateBinanceSignature(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Summarize(string body)
    {
        const int maxLength = 240;
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var compact = body.ReplaceLineEndings(" ").Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength];
    }
}
