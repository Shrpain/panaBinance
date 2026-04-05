using Binace.Trader.Api.Hubs;
using Binace.Trader.Application.Abstractions;
using Binace.Trader.Application.Contracts;
using Binace.Trader.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Binace.Trader.Domain.Services;
using Binace.Trader.Infrastructure.Services;
using Binace.Trader.Domain.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();
builder.Services.AddSingleton<Binace.Trader.Domain.Services.PositionManager>();
builder.Services.AddSingleton<Binace.Trader.Domain.Services.BacktestEngine>();
builder.Services.AddHostedService<Binace.Trader.Api.Services.BinanceDataPollingService>();
builder.Services.AddHostedService<Binace.Trader.Api.Services.BinanceBotService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "frontend",
        policy =>
        {
            var origins = builder.Configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"];
            policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
});

builder.Services.AddInfrastructureServices();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors("frontend");

var api = app.MapGroup("/api").WithTags("Trader API");

api.MapGet("/health", () =>
        TypedResults.Ok(new
        {
            status = "ok",
            service = "binace-trader-api",
            timeUtc = DateTimeOffset.UtcNow,
        }))
    .WithName("GetApiHealth");

var dashboard = api.MapGroup("/dashboard").WithTags("Dashboard");

dashboard.MapGet("/status", async Task<Ok<DashboardSnapshotDto>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetSnapshotAsync(cancellationToken)))
    .WithName("GetDashboardStatus");

dashboard.MapGet("/positions", async Task<Ok<IReadOnlyList<OpenPositionDto>>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetOpenPositionsAsync(cancellationToken)))
    .WithName("GetOpenPositions");

dashboard.MapGet("/config", async Task<Ok<BotConfigurationDto>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetConfigurationAsync(cancellationToken)))
    .WithName("GetBotConfiguration");

dashboard.MapGet("/integrations", async Task<Ok<IntegrationSettingsDto>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetIntegrationSettingsAsync(cancellationToken)))
    .WithName("GetIntegrationSettings");

dashboard.MapGet("/balances", async Task<Ok<IReadOnlyList<AccountBalanceDto>>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetAccountBalancesAsync(cancellationToken)))
    .WithName("GetAccountBalances");

dashboard.MapGet("/trades", async Task<Ok<IReadOnlyList<TradeHistoryDto>>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetTradeHistoryAsync(cancellationToken)))
    .WithName("GetTradeHistory");

dashboard.MapGet("/tickers", async Task<Ok<IReadOnlyList<TickerPriceDto>>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetTickerPricesAsync(cancellationToken)))
    .WithName("GetTickerPrices");

dashboard.MapPost("/test/binance", async Task<Ok<ConnectionTestResultDto>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.TestBinanceConnectionAsync(cancellationToken)))
    .WithName("TestBinanceConnection");

dashboard.MapPost("/test/telegram", async Task<Ok<ConnectionTestResultDto>> (ITradingDashboardService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.TestTelegramConnectionAsync(cancellationToken)))
    .WithName("TestTelegramConnection");

dashboard.MapPost("/test/zalo", async Task<IResult> (
        INotificationService notificationService,
        ITradingDashboardService dashboardService,
        CancellationToken cancellationToken) =>
    {
        var settings = await dashboardService.GetIntegrationSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ZaloToken))
        {
            return TypedResults.BadRequest("Zalo token missing in tt.txt");
        }

        var testMsg = "TEST NOTIFICATION\n" +
                      "da mo lenh: Buy\n" +
                      "coin: BTCUSDT\n" +
                      "sl: 65200.50\n" +
                      "tp: 68500.00\n" +
                      "pnl: 0.00\n" +
                      "Roi: 0.00%\n";
        
        await notificationService.SendNotificationAsync(testMsg, cancellationToken);
        return TypedResults.Ok(new { message = "Test Zalo sent", tokenUsed = settings.ZaloToken });
    })
    .WithName("TestZaloConnection");

dashboard.MapPost("/backtest", async Task<Ok<BacktestResult>> (
        string symbol, 
        string interval, 
        int limit, 
        decimal leverage, 
        decimal margin,
        string strategy,
        BinanceApiClient apiClient,
        ITradingDashboardService dashboardService,
        BacktestEngine engine,
        CancellationToken ct) =>
    {
        var klines = await apiClient.GetFuturesKlinesAsync(symbol, interval, limit, ct);
        
        // Fetch current balance to calculate effective margin if margin is treated as %
        var balances = await dashboardService.GetAccountBalancesAsync(ct);
        var usdtBalance = balances.FirstOrDefault(b => string.Equals(b.Asset, "USDT", StringComparison.OrdinalIgnoreCase))?.Free ?? 1000m;
        
        // If balance is 0 or very low, default to 1000 for simulation purposes
        if (usdtBalance <= 0) usdtBalance = 1000m;
        
        decimal effectiveMargin = usdtBalance * (margin / 100m);
        
        var result = engine.Run(symbol, klines.ToList(), leverage, effectiveMargin, strategy);
        return TypedResults.Ok(result);
    })
    .WithName("RunBacktest");

dashboard.MapPut("/config", async Task<Results<Ok<BotConfigurationDto>, ValidationProblem>> (
        UpdateBotConfigurationRequest request,
        ITradingDashboardService service,
        IHubContext<BotUpdatesHub> hubContext,
        CancellationToken cancellationToken) =>
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return TypedResults.ValidationProblem(validationErrors);
        }

        var updated = await service.SaveConfigurationAsync(request, cancellationToken);
        await hubContext.Clients.All.SendAsync("botConfigUpdated", updated, cancellationToken);

        return TypedResults.Ok(updated);
    })
    .WithName("UpdateBotConfiguration");

var bot = api.MapGroup("/bot").WithTags("Bot Runtime");

bot.MapGet("/runtime", Ok<BotRuntimeStateDto> (IBotRuntimeService runtimeService) =>
        TypedResults.Ok(runtimeService.GetState()))
    .WithName("GetBotRuntime");

bot.MapPut("/runtime/settings", Results<Ok<BotRuntimeStateDto>, ValidationProblem> (UpdateBotRuntimeSettingsRequestDto request, IBotRuntimeService runtimeService) =>
    {
        var validationErrors = ValidateRuntimeSettings(request);
        if (validationErrors.Count > 0)
        {
            return TypedResults.ValidationProblem(validationErrors);
        }

        return TypedResults.Ok(runtimeService.UpdateSettings(request));
    })
    .WithName("UpdateBotRuntimeSettings");

bot.MapPost("/trader-coins", Results<Ok<BotRuntimeStateDto>, ValidationProblem> (TraderCoinRequestDto request, IBotRuntimeService runtimeService) =>
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Symbol)] = ["Symbol is required."]
            });
        }

        return TypedResults.Ok(runtimeService.AddTraderCoin(request.Symbol));
    })
    .WithName("AddTraderCoin");

bot.MapDelete("/trader-coins/{symbol}", Ok<BotRuntimeStateDto> (string symbol, IBotRuntimeService runtimeService) =>
        TypedResults.Ok(runtimeService.RemoveTraderCoin(symbol)))
    .WithName("RemoveTraderCoin");

bot.MapGet("/events", Ok<IReadOnlyList<BotTerminalEventDto>> (IBotTerminalEventService eventService) =>
        TypedResults.Ok(eventService.GetRecentEvents()))
    .WithName("GetBotTerminalEvents");

bot.MapPost("/start", async Task<Results<Ok<BotRuntimeStateDto>, ValidationProblem>> (
        IBotRuntimeService runtimeService,
        IBotTerminalEventService eventService,
        IHubContext<BotUpdatesHub> hubContext,
        CancellationToken cancellationToken) =>
    {
        var state = runtimeService.GetState();
        if (state.TraderSymbols.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["traderSymbols"] = ["Add at least one coin before starting the bot."]
            });
        }

        var started = runtimeService.Start();
        await PublishBotTerminalEventAsync(eventService, hubContext, "success", "chạy bot thành công", cancellationToken);
        await PublishBotTerminalEventAsync(eventService, hubContext, "success", "chế độ trade thật: ✔", cancellationToken);
        return TypedResults.Ok(started);
    })
    .WithName("StartBotRuntime");

bot.MapPost("/stop", async Task<Ok<BotRuntimeStateDto>> (
        IBotRuntimeService runtimeService,
        IBotTerminalEventService eventService,
        IHubContext<BotUpdatesHub> hubContext,
        CancellationToken cancellationToken) =>
    {
        var stopped = runtimeService.Stop();
        await PublishBotTerminalEventAsync(eventService, hubContext, "info", "bot đã dừng", cancellationToken);
        return TypedResults.Ok(stopped);
    })
    .WithName("StopBotRuntime");

app.MapHealthChecks("/health");
app.MapHub<BotUpdatesHub>("/hubs/bot-updates");

app.Run();

static Dictionary<string, string[]> Validate(UpdateBotConfigurationRequest request)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.Symbol))
    {
        errors[nameof(request.Symbol)] = ["Symbol is required."];
    }

    if (string.IsNullOrWhiteSpace(request.Strategy))
    {
        errors[nameof(request.Strategy)] = ["Strategy is required."];
    }

    if (request.RiskPerTradePercent <= 0 || request.RiskPerTradePercent > 5)
    {
        errors[nameof(request.RiskPerTradePercent)] = ["RiskPerTradePercent must be between 0 and 5."];
    }

    return errors;
}

static Dictionary<string, string[]> ValidateRuntimeSettings(UpdateBotRuntimeSettingsRequestDto request)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.Interval))
    {
        errors[nameof(request.Interval)] = ["Interval is required."];
    }

    if (request.Leverage <= 0 || request.Leverage > 125)
    {
        errors[nameof(request.Leverage)] = ["Leverage must be between 1 and 125."];
    }

    if (request.Margin <= 0)
    {
        errors[nameof(request.Margin)] = ["Margin must be greater than 0."];
    }

    return errors;
}

static async Task PublishBotTerminalEventAsync(
    IBotTerminalEventService eventService,
    IHubContext<BotUpdatesHub> hubContext,
    string level,
    string message,
    CancellationToken cancellationToken)
{
    var item = eventService.Add(level, message);
    await hubContext.Clients.All.SendAsync("botEventReceived", item, cancellationToken);
}

public partial class Program;
