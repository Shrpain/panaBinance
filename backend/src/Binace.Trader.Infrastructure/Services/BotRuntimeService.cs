using System.Text.Json;
using Binace.Trader.Application.Abstractions;
using Binace.Trader.Application.Contracts;
using Microsoft.Extensions.Hosting;

namespace Binace.Trader.Infrastructure.Services;

public sealed class BotRuntimeService : IBotRuntimeService
{
    private readonly Lock _gate = new();
    private readonly string _stateFilePath;
    private BotRuntimeStateDto _state;

    public BotRuntimeService(IHostEnvironment hostEnvironment)
    {
        var workspaceRoot = ResolveWorkspaceRoot(hostEnvironment.ContentRootPath);
        var stateDirectory = Path.Combine(workspaceRoot, ".omx", "state");
        Directory.CreateDirectory(stateDirectory);

        _stateFilePath = Path.Combine(stateDirectory, "binance-bot-runtime.json");
        _state = LoadState();
    }

    public BotRuntimeStateDto GetState()
    {
        lock (_gate)
        {
            return Clone(_state);
        }
    }

    public BotRuntimeStateDto AddTraderCoin(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return GetState();
        }

        lock (_gate)
        {
            if (!_state.TraderSymbols.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                var symbols = _state.TraderSymbols.Append(normalized).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                _state = _state with { TraderSymbols = symbols };
                SaveState();
            }

            return Clone(_state);
        }
    }

    public BotRuntimeStateDto RemoveTraderCoin(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);
        lock (_gate)
        {
            var symbols = _state.TraderSymbols.Where(value => !string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
            _state = _state with { TraderSymbols = symbols };
            SaveState();

            return Clone(_state);
        }
    }

    public BotRuntimeStateDto UpdateSettings(UpdateBotRuntimeSettingsRequestDto request)
    {
        lock (_gate)
        {
            _state = _state with
            {
                Interval = string.IsNullOrWhiteSpace(request.Interval) ? _state.Interval : request.Interval.Trim(),
                Leverage = request.Leverage > 0 ? request.Leverage : _state.Leverage,
                Margin = request.Margin > 0 ? request.Margin : _state.Margin,
                Strategy = string.IsNullOrWhiteSpace(request.Strategy) ? _state.Strategy : request.Strategy.Trim(),
            };
            SaveState();

            return Clone(_state);
        }
    }

    public BotRuntimeStateDto Start()
    {
        lock (_gate)
        {
            _state = _state with { IsRunning = true };
            SaveState();

            return Clone(_state);
        }
    }

    public BotRuntimeStateDto Stop()
    {
        lock (_gate)
        {
            _state = _state with { IsRunning = false };
            SaveState();

            return Clone(_state);
        }
    }

    private BotRuntimeStateDto LoadState()
    {
        if (!File.Exists(_stateFilePath))
        {
            var initial = DefaultState();
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(initial, SerializerOptions));
            return initial;
        }

        try
        {
            var raw = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<BotRuntimeStateDto>(raw, SerializerOptions);
            return state is null ? DefaultState() : NormalizeState(state);
        }
        catch
        {
            return DefaultState();
        }
    }

    private void SaveState()
    {
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(_state, SerializerOptions));
    }

    private static BotRuntimeStateDto DefaultState() => new(
        IsRunning: false,
        Interval: "5m",
        Leverage: 20,
        Margin: 15m,
        Strategy: "UT Bot Alerts",
        TraderSymbols: []);

    private static BotRuntimeStateDto NormalizeState(BotRuntimeStateDto state)
    {
        var symbols = state.TraderSymbols
            .Select(NormalizeSymbol)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BotRuntimeStateDto(
            IsRunning: state.IsRunning,
            Interval: string.IsNullOrWhiteSpace(state.Interval) ? "5m" : state.Interval.Trim(),
            Leverage: state.Leverage <= 0 ? 20 : state.Leverage,
            Margin: state.Margin <= 0 ? 15m : state.Margin,
            Strategy: string.IsNullOrWhiteSpace(state.Strategy) ? "UT Bot Alerts" : state.Strategy.Trim(),
            TraderSymbols: symbols);
    }

    private static BotRuntimeStateDto Clone(BotRuntimeStateDto state) => state with
    {
        TraderSymbols = state.TraderSymbols.ToArray()
    };

    private static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();

    private static string ResolveWorkspaceRoot(string contentRootPath)
    {
        var current = new DirectoryInfo(contentRootPath);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".omx")) || File.Exists(Path.Combine(current.FullName, "tt.txt")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return contentRootPath;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
}
