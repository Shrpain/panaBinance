namespace Binace.Trader.Application.Contracts;

public sealed record TradeHistoryDto(
    long Id,
    string Symbol,
    string Side,
    decimal Price,
    decimal Quantity,
    decimal QuoteQuantity,
    decimal Commission,
    string CommissionAsset,
    decimal RealizedPnl,
    DateTimeOffset Time);
