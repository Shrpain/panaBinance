using System;

namespace Binace.Trader.Domain.Models;

public enum PositionSide
{
    None,
    Long,
    Short
}

public class PositionState
{
    public string Symbol { get; set; } = string.Empty;
    public PositionSide Side { get; set; } = PositionSide.None;
    public decimal EntryPrice { get; set; }
    public decimal InitialQuantity { get; set; }
    public decimal Quantity { get; set; }
    public decimal MarginUsed { get; set; }
    public int Leverage { get; set; }
    public decimal InitialStopLoss { get; set; }
    public decimal TakeProfit1 { get; set; }
    
    // Risk Management Trackers
    public bool Tp1Hit { get; set; }
    public decimal CurrentTrailingStop { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal TotalFees { get; set; }
    
    // Computed states
    public bool IsActive => Side != PositionSide.None && Quantity > 0;
}
