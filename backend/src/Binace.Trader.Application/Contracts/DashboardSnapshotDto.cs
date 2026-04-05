namespace Binace.Trader.Application.Contracts;

public sealed record DashboardSnapshotDto(
    string Exchange,
    string Symbol,
    string Strategy,
    string Status,
    DateTimeOffset LastHeartbeatUtc,
    decimal DailyPnl,
    decimal UnrealizedPnl,
    decimal WinRate,
    int OpenPositions,
    IReadOnlyList<MetricPointDto> EquityCurve);
