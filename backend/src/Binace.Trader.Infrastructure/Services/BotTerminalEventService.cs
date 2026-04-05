using System.Text.Json;
using Binace.Trader.Application.Abstractions;
using Binace.Trader.Application.Contracts;
using Microsoft.Extensions.Hosting;

namespace Binace.Trader.Infrastructure.Services;

public sealed class BotTerminalEventService : IBotTerminalEventService
{
    private const int MaxEvents = 300;

    private readonly Lock _gate = new();
    private readonly string _stateFilePath;
    private List<BotTerminalEventDto> _events;

    public BotTerminalEventService(IHostEnvironment hostEnvironment)
    {
        var workspaceRoot = ResolveWorkspaceRoot(hostEnvironment.ContentRootPath);
        var stateDirectory = Path.Combine(workspaceRoot, ".omx", "state");
        Directory.CreateDirectory(stateDirectory);

        _stateFilePath = Path.Combine(stateDirectory, "binance-bot-terminal-events.json");
        _events = LoadState();
    }

    public IReadOnlyList<BotTerminalEventDto> GetRecentEvents()
    {
        lock (_gate)
        {
            return _events.OrderBy(eventItem => eventItem.Id).ToArray();
        }
    }

    public BotTerminalEventDto Add(string level, string message)
    {
        lock (_gate)
        {
            var nextId = _events.Count == 0 ? 1 : _events[^1].Id + 1;
            var item = new BotTerminalEventDto(
                Id: nextId,
                TimeUtc: DateTimeOffset.UtcNow,
                Level: level,
                Message: message);

            _events.Add(item);
            if (_events.Count > MaxEvents)
            {
                _events = _events.Skip(_events.Count - MaxEvents).ToList();
            }

            SaveState();
            return item;
        }
    }

    private List<BotTerminalEventDto> LoadState()
    {
        if (!File.Exists(_stateFilePath))
        {
            File.WriteAllText(_stateFilePath, "[]");
            return [];
        }

        try
        {
            var raw = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<List<BotTerminalEventDto>>(raw, SerializerOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveState()
    {
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(_events, SerializerOptions));
    }

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
