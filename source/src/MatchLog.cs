using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WtMfd;

/// <summary>
/// Records what War Thunder sends, one file per match, in logs\ next to the app.
/// Each line is one JSON object: { "t": local time, "kind": what it is, "data": the raw game data }.
/// Used to find bugs and to build new features from real match data (chat formats, map objects, mission changes).
/// </summary>
public sealed class MatchLog : IDisposable
{
    private const long MaxBytesPerFile = 50L * 1024 * 1024; // a runaway match can't fill the disk
    private const int KeepFiles = 200;

    private readonly object _lock = new();
    private readonly bool _enabled;
    private readonly string _dir;
    private StreamWriter? _w;
    private long _bytes;
    private bool _full;
    private string _label = "";
    private string? _path;
    private bool _matched; // the open file already belongs to a match (it was not just opened at app start)
    // keep names, tags and non-Latin text readable in the file instead of \u003C-style escapes
    private static readonly JsonSerializerOptions Raw = new(Json.Opts) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public MatchLog(Settings s)
    {
        _enabled = s.Log;
        _dir = Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public string Folder => _dir;

    /// <summary>Close the current file; the next write starts a new one.</summary>
    public void NewMatch(string reason, string label = "")
    {
        if (!_enabled) return;
        lock (_lock)
        {
            _label = label;
            if (_w != null && !_matched && _path != null)
            {
                // the app was started before this match: keep what was already written and give the file the match's name
                Close();
                try { var to = Path.Combine(_dir, Path.GetFileNameWithoutExtension(_path) + Suffix() + ".jsonl"); File.Move(_path, to); _path = to; } catch { }
                Open(reason, _path);
            }
            else
            {
                Close();
                Open(reason);
            }
            _matched = true;
        }
    }

    public void Write(string kind, JsonNode? data) => WriteRaw(kind, data?.ToJsonString(Raw) ?? "null");

    public void Write(string kind, object data) => WriteRaw(kind, JsonSerializer.Serialize(data, Raw));

    private void WriteRaw(string kind, string dataJson)
    {
        if (!_enabled) return;
        lock (_lock)
        {
            try
            {
                if (_w == null) Open("app started");
                if (_full || _w == null) return;
                var line = $"{{\"t\":\"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}\",\"kind\":{JsonSerializer.Serialize(kind)},\"data\":{dataJson}}}";
                _w.WriteLine(line);
                _bytes += Encoding.UTF8.GetByteCount(line) + 2;
                if (_bytes > MaxBytesPerFile)
                {
                    _w.WriteLine($"{{\"t\":\"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}\",\"kind\":\"log-full\",\"data\":null}}");
                    _full = true;
                }
            }
            catch { /* logging must never break the MFD */ }
        }
    }

    private string Suffix()
    {
        var safe = string.Concat(_label.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        return safe.Length > 0 ? "_" + safe : "";
    }

    private void Open(string reason, string? path = null)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            if (path == null) Prune();
            _path = path ?? Path.Combine(_dir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{Suffix()}.jsonl");
            _w = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            _bytes = 0; _full = false;
            _w.WriteLine($"{{\"t\":\"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}\",\"kind\":\"start\",\"data\":{{\"reason\":{JsonSerializer.Serialize(reason)},\"app\":\"WT-MFD\"}}}}");
        }
        catch { _w = null; }
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.jsonl").OrderByDescending(f => f.Name).Skip(KeepFiles - 1);
            foreach (var f in files) f.Delete();
        }
        catch { }
    }

    private void Close()
    {
        try { _w?.Dispose(); } catch { }
        _w = null;
    }

    public void Dispose() { lock (_lock) Close(); }
}
