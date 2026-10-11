using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace WtMfd;

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Message(string type, object? data) => JsonSerializer.Serialize(new { type, data }, Opts);
}

/// <summary>Fans messages out to every open MFD page (Server-Sent Events).</summary>
public sealed class Hub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    public int Count => _clients.Count;

    public (Guid Id, ChannelReader<string> Reader) Join()
    {
        // a slow screen drops old frames instead of building a backlog
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _clients[id] = ch;
        return (id, ch.Reader);
    }

    public void Leave(Guid id)
    {
        if (_clients.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public void Send(string type, object? data)
    {
        if (_clients.IsEmpty) return;
        var json = Json.Message(type, data);
        foreach (var c in _clients.Values) c.Writer.TryWrite(json);
    }
}
