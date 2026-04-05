using Binace.Trader.Application.Contracts;

namespace Binace.Trader.Application.Abstractions;

public interface ITradingDashboardService
{
    Task<DashboardSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OpenPositionDto>> GetOpenPositionsAsync(CancellationToken cancellationToken);

    Task<BotConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken);

    Task<IntegrationSettingsDto> GetIntegrationSettingsAsync(CancellationToken cancellationToken);

    Task<ConnectionTestResultDto> TestBinanceConnectionAsync(CancellationToken cancellationToken);

    Task<ConnectionTestResultDto> TestTelegramConnectionAsync(CancellationToken cancellationToken);

    Task<BotConfigurationDto> SaveConfigurationAsync(UpdateBotConfigurationRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountBalanceDto>> GetAccountBalancesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TradeHistoryDto>> GetTradeHistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TickerPriceDto>> GetTickerPricesAsync(CancellationToken cancellationToken);
}
