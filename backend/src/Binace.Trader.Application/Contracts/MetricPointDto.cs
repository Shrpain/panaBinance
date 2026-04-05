namespace Binace.Trader.Application.Contracts;

public sealed record MetricPointDto(DateTimeOffset TimestampUtc, decimal Value);
