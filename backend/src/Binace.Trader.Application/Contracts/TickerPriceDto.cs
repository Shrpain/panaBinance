namespace Binace.Trader.Application.Contracts;

public sealed record TickerPriceDto(
    string Symbol,
    decimal Price,
    decimal PriceChangePercent24h,
    decimal Volume24h);
