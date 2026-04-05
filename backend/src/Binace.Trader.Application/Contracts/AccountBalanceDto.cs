namespace Binace.Trader.Application.Contracts;

public sealed record AccountBalanceDto(
    string Asset,
    string Market,
    decimal Free,
    decimal Locked,
    decimal WalletBalance,
    decimal UnrealizedPnl);
