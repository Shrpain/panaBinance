using Binace.Trader.Application.Abstractions;
using Binace.Trader.Application.Contracts;
using Binace.Trader.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Binace.Trader.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddHttpClient("external");
        services.AddSingleton<IBotRuntimeService, BotRuntimeService>();
        services.AddSingleton<IBotTerminalEventService, BotTerminalEventService>();

        services.AddSingleton<BinanceApiClient>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BinanceApiClient>>();

            var settings = LoadSettings(env.ContentRootPath);
            return new BinanceApiClient(httpFactory, logger, settings.ApiKey, settings.ApiSecret);
        });

        services.AddSingleton<ITradingDashboardService, InMemoryTradingDashboardService>();
        services.AddSingleton<INotificationService, NotificationService>();

        return services;
    }

    private static IntegrationSettingsDto LoadSettings(string contentRootPath)
    {
        var current = new DirectoryInfo(contentRootPath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tt.txt");
            if (File.Exists(candidate))
            {
                var lines = File.ReadAllLines(candidate);
                return TtFileParser.Parse(lines, candidate);
            }
            current = current.Parent;
        }

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
}
