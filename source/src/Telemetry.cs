using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace WtMfd;

/// <summary>
/// Polls War Thunder's own local API (http://localhost:8111) and forwards it to the MFD pages.
/// It only reads what the game publishes for its built-in web map and instruments; nothing is added.
/// </summary>
public sealed class Telemetry : BackgroundService
{
    private readonly HttpClient _http;
    private readonly Hub _hub;
    private readonly UiState _ui;
    private readonly Settings _s;
    private readonly MatchLog _log;
    private readonly object _lock = new();

    // latest raw data
    private JsonNode? _state, _ind, _mapObjs, _mapInfo, _mission;
    private string _status = "offline";            // offline | hangar | flying
    private string _aircraft = "";
    private byte[]? _mapImg;
    private string _mapImgType = "image/jpeg";
    private int _mapVersion;
    private string _mapInfoText = "", _missionText = "", _mapHash = "";

    // derived values
    private double _gMin = double.NaN, _gMax = double.NaN;
    private readonly Queue<(double T, double Kg)> _fuel = new();
    private double _burnKgMin = double.NaN;

    // event log
    private readonly List<EventItem> _events = new();
    private long _lastEvt, _lastDmg, _lastChat, _seq;
    private int _misses;                           // game requests in a row that got no answer
    private readonly HashSet<string> _seenMsgs = new(); // messages already shown this match, so a re-read never repeats them
    private int _eventPolls;                       // counts PollEvents calls, to re-check the game's message ids now and then
    private DateTime _lastStateLog = DateTime.MinValue, _lastMapLog = DateTime.MinValue;
    private readonly HashSet<string> _seenKinds = new(); // map object kinds already written to this match's log
    private volatile bool _logMapNext;                  // a team call came in: record where everything is on the map right now
    private bool _chatPrimed; // false until the first chat poll after (re)connecting: everything in that one is old
    private bool _dmgPrimed;  // same for the kill feed: deaths from before WT-MFD connected must not end the reward cycle

    // reward cycle (Air Simulator pays out every 15 minutes of life)
    public RewardCycle Cycle { get; }
    private volatile bool _inMatch;                 // the game has a battle map loaded (test flights included)
    private DateTime _offlineSince = DateTime.MinValue;
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Telemetry(Hub hub, UiState ui, Settings s, MatchLog log)
    {
        _hub = hub; _ui = ui; _s = s; _log = log;
        Cycle = new RewardCycle(s, (kind, data) => _log.Write(kind, data));
        // War Thunder listens on IPv4 only. Going straight to 127.0.0.1 skips Windows' slow failed IPv6 attempt on "localhost".
        var url = new UriBuilder(s.WarThunderUrl.TrimEnd('/') + "/");
        if (url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) url.Host = "127.0.0.1";
        var handler = new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30), ConnectTimeout = TimeSpan.FromMilliseconds(500), UseProxy = false };
        _http = new HttpClient(handler) { BaseAddress = url.Uri, Timeout = TimeSpan.FromMilliseconds(900) };
    }

    /// <param name="Own">Sent by you (sender is your Callsign).</param>
    /// <param name="Backlog">Already in the game's chat when WT-MFD connected, so not a fresh call.</param>
    public record EventItem(long Id, long T, string Kind, string Msg, string Sender, bool Enemy, string Mode, bool Me, bool Own = false, bool Backlog = false);

    // ------------------------------------------------------------------ public API used by commands and new screens
    public List<string> Snapshot()
    {
        var list = new List<string>();
        lock (_lock)
        {
            list.Add(Json.Message("ui", _ui.Snapshot()));
            list.Add(Json.Message("status", StatusObj()));
            if (_status == "flying" && _state != null && _ind != null) list.Add(Json.Message("tele", TeleObj()));
            if (_mapInfo != null) list.Add(Json.Message("mapinfo", new { info = _mapInfo, version = _mapVersion, hasImage = _mapImg != null }));
            if (_mapObjs != null) list.Add(Json.Message("map", _mapObjs));
            if (_mission != null) list.Add(Json.Message("mission", _mission));
            list.Add(Json.Message("events", new { reset = true, items = _events.TakeLast(200).ToList() }));
        }
        return list;
    }

    public (byte[]? Bytes, string Type) MapImage() { lock (_lock) return (_mapImg, _mapImgType); }

    public void ResetG() { lock (_lock) { _gMin = double.NaN; _gMax = double.NaN; } }
    public void ResetBurn() { lock (_lock) { _fuel.Clear(); _burnKgMin = double.NaN; } }
    /// <summary>CYC RST: start a fresh reward cycle now, and tell every screen at once.</summary>
    public void ResetCycle()
    {
        Cycle.Reset(NowMs);
        lock (_lock) _hub.Send("status", StatusObj());
    }

    public void StopCycle()
    {
        Cycle.Stop("CYC OFF", NowMs);
        lock (_lock) _hub.Send("status", StatusObj());
    }

    public void ClearEvents()
    {
        lock (_lock) _events.Clear();
        _hub.Send("events", new { reset = true, items = Array.Empty<EventItem>() });
    }

    // ------------------------------------------------------------------ loops
    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(Task.Run(() => FastLoop(ct), ct), Task.Run(() => SlowLoop(ct), ct));

    /// <summary>Flight data, 10 times a second.</summary>
    private async Task FastLoop(CancellationToken ct)
    {
        var lastStatusSent = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            var stateTask = Get("state", ct);
            var indTask = Get("indicators", ct);
            var state = await stateTask;
            var ind = await indTask;

            // one slow answer (the game is busy loading or saving) is not the game closing: only call it offline
            // after three misses in a row, or the MFD blinks offline and re-reads every message mid-match
            string status;
            if (state == null || ind == null)
            {
                _misses++;
                string prev; lock (_lock) prev = _status;
                status = _misses >= 3 ? "offline" : prev;
                if (status != "offline") { await Task.Delay(100, ct).ContinueWith(_ => { }); continue; }
            }
            else
            {
                _misses = 0;
                // Flying = the flight model is live. The instrument feed (/indicators) is optional: since the game's
                // 9 Oct 2026 restart it answers {"valid": false} all the time, and the MFD should still fly on /state.
                status = Bool(state, "valid") ? "flying" : "hangar";
            }

            bool statusChanged;
            object? tele = null;
            lock (_lock)
            {
                statusChanged = status != _status;
                // the game swaps the vehicle (spawn, respawn screen, back to hangar) with a split-second "not flying"
                var swapped = status == "flying" && _status != "flying";
                _status = status;
                if (status == "flying")
                {
                    _state = state; _ind = ind;
                    Derive(swapped);
                    tele = TeleObj();
                }
            }
            // the game closed: the reward cycle is over
            if (status == "offline")
            {
                if (_offlineSince == DateTime.MinValue) _offlineSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - _offlineSince > TimeSpan.FromSeconds(15)) { _inMatch = false; Cycle.Stop("game closed", NowMs); }
            }
            else _offlineSince = DateTime.MinValue;
            if (tele != null) _hub.Send("tele", tele);
            if (statusChanged) { string ac; lock (_lock) ac = _aircraft; _log.Write("status", new { status, aircraft = ac }); }
            if (status == "flying" && DateTime.UtcNow - _lastStateLog > TimeSpan.FromSeconds(10))
            {
                _lastStateLog = DateTime.UtcNow;
                _log.Write("state", state);
                _log.Write("indicators", ind);
            }
            if (statusChanged || DateTime.UtcNow - lastStatusSent > TimeSpan.FromSeconds(1))
            {
                lock (_lock) _hub.Send("status", StatusObj());
                lastStatusSent = DateTime.UtcNow;
            }

            var wait = 100 - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            await Task.Delay(Math.Max(status == "offline" ? 900 : 10, wait), ct).ContinueWith(_ => { });
        }
    }

    /// <summary>Map, events and mission, a few times a second.</summary>
    private async Task SlowLoop(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            string status;
            lock (_lock) status = _status;
            if (status != "offline")
            {
                try
                {
                    if (tick % 2 == 0) await PollMap(ct);
                    if (tick % 8 == 0) await PollMapInfo(ct, forceImage: tick % 60 == 0);
                    if (tick % 4 == 0) await PollEvents(ct);
                    if (tick % 4 == 2) await PollMission(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException) { _log.Write("error", new { where = "slow loop", e.Message }); /* the game can drop mid-request; try again next tick */ }
            }
            tick++;
            await Task.Delay(250, ct).ContinueWith(_ => { });
        }
    }

    /// <summary>
    /// NEXT TGT: select the next enemy object on the map, nearest to you first. Each press moves the one target on;
    /// it never builds a route. Enemy = red on the game's map (ground units, bombing points, ships, airfields...).
    /// </summary>
    private bool ClearTarget() { var had = _ui.Target != null; _ui.SetTarget(null); return had; }

    public void NextTarget()
    {
        List<(double X, double Y, string Icon)> list = new();
        double mx = double.NaN, my = double.NaN;
        lock (_lock)
        {
            if (_mapObjs is not JsonArray objs) return;
            foreach (var o in objs)
            {
                if (o == null) continue;
                var icon = Str(o, "icon"); var type = Str(o, "type");
                if (icon == "Player") { mx = Num(o, "x"); my = Num(o, "y"); continue; }
                if (type == "capture_zone" || !IsEnemy(o)) continue;
                double x = Num(o, "x"), y = Num(o, "y");
                if (type == "airfield" && double.IsNaN(x)) { x = (Num(o, "sx") + Num(o, "ex")) / 2; y = (Num(o, "sy") + Num(o, "ey")) / 2; icon = "airfield"; }
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                list.Add((x, y, icon.Length > 0 ? icon : type));
            }
        }
        if (list.Count == 0) { _ui.SetTarget(null); return; }
        if (!double.IsNaN(mx)) list = list.OrderBy(t => Math.Pow(t.X - mx, 2) + Math.Pow(t.Y - my, 2)).ToList();
        else list = list.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        // where is the current target in the list? (it may have moved a little: take the closest of the same kind)
        var next = 0;
        if (_ui.Target is { } cur)
        {
            var best = 0.03; var at = -1;
            for (var i = 0; i < list.Count; i++)
            {
                var d = Math.Sqrt(Math.Pow(list[i].X - cur.X, 2) + Math.Pow(list[i].Y - cur.Y, 2));
                if (string.Equals(list[i].Icon, cur.Icon, StringComparison.OrdinalIgnoreCase) && d < best) { best = d; at = i; }
            }
            next = at >= 0 ? (at + 1) % list.Count : 0;
        }
        var t = list[next];
        _ui.SetTarget(new UiState.TargetMark(t.X, t.Y, t.Icon));
    }

    /// <summary>Red on the game's map (#fa0C00, #f00C00...) is the enemy; blue is friendly, yellow is you.</summary>
    private static bool IsEnemy(JsonNode o)
    {
        int r, g, b;
        if (o["color[]"] is JsonArray c && c.Count >= 3) { r = (int)Num(c, 0); g = (int)Num(c, 1); b = (int)Num(c, 2); }
        else
        {
            var hex = Str(o, "color").TrimStart('#');
            if (hex.Length < 6 || !int.TryParse(hex[^6..], System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
            r = (v >> 16) & 255; g = (v >> 8) & 255; b = v & 255;
        }
        return r >= 180 && g <= 90 && b <= 90;
    }
    private static double Num(JsonArray a, int i) => a[i] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;

    /// <summary>Bingo as a share of this aircraft's full internal fuel.</summary>
    public void BingoFraction(double f)
    {
        double max;
        lock (_lock) max = _state == null ? double.NaN : Num(_state, "Mfuel0, kg");
        if (!double.IsNaN(max) && max > 0) _ui.SetBingo(max * f);
    }

    private async Task PollMap(CancellationToken ct)
    {
        var objs = await Get("map_obj.json", ct);
        if (objs is not JsonArray arr) return;
        lock (_lock) _mapObjs = objs;
        _hub.Send("map", objs);

        // log: every new kind of map object once per match, the whole map every 30 s, and right after a team call
        foreach (var o in arr)
        {
            if (o == null) continue;
            // capture zones fade colour while being captured: log each zone once, not every shade
            var kind = Str(o, "type") == "capture_zone"
                ? $"capture_zone|{o["zone_id"]}"
                : $"{Str(o, "type")}|{Str(o, "icon")}|{Str(o, "color")}|{o["blink"]}";
            bool isNew;
            lock (_lock) isNew = _seenKinds.Add(kind);
            if (isNew) _log.Write("map-new-kind", o);
        }
        if (_logMapNext || DateTime.UtcNow - _lastMapLog > TimeSpan.FromSeconds(30))
        {
            _log.Write(_logMapNext ? "map-at-call" : "map", objs);
            _logMapNext = false;
            _lastMapLog = DateTime.UtcNow;
        }
    }

    private async Task PollMapInfo(CancellationToken ct, bool forceImage)
    {
        var info = await Get("map_info.json", ct);
        if (info == null) return;
        // between matches the game answers {"valid": false}: not a new map, keep the last one (and the match log)
        if (info["valid"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok)
        {
            if (_inMatch) { _inMatch = false; Cycle.Stop("left the battle", NowMs); }
            return;
        }
        _inMatch = true;
        var text = info.ToJsonString();
        bool changed;
        lock (_lock) changed = text != _mapInfoText;
        if (!changed && !forceImage) return;

        // the map can change between battles without the info changing, so the image is also re-checked now and then
        var bytes = await GetBytes("map.img", ct);
        var hash = bytes == null ? "" : Convert.ToHexString(SHA1.HashData(bytes));
        object? msg = null;
        lock (_lock)
        {
            if (changed || hash != _mapHash)
            {
                _mapInfoText = text; _mapInfo = info; _mapHash = hash;
                _mapImg = bytes;
                _mapImgType = bytes is { Length: > 3 } && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : "image/jpeg";
                _mapVersion++;
                msg = new { info = _mapInfo, version = _mapVersion, hasImage = _mapImg != null };
            }
        }
        if (msg != null)
        {
            string ac; lock (_lock) { ac = _aircraft; _seenKinds.Clear(); _seenMsgs.Clear(); }
            if (changed && (_ui.ClearWaypoints() | ClearTarget())) _hub.Send("ui", _ui.Snapshot()); // they belong to the old map
            _log.NewMatch(changed ? "map changed" : "map picture changed", ac);
            _log.Write("mapinfo", info);
            _hub.Send("mapinfo", msg);
        }
    }

    private async Task PollEvents(CancellationToken ct)
    {
        long le, ld, lc;
        lock (_lock) { le = _lastEvt; ld = _lastDmg; lc = _lastChat; }
        var fresh = new List<EventItem>();

        // War Thunder may number messages from 1 again in a new match. If the ids we wait for are now higher than
        // anything the game has, start over, or the next match's chat and kill feed would never show.
        if (++_eventPolls % 10 == 0 && (lc > 0 || ld > 0 || le > 0))
        {
            var allChat = await Get("gamechat?lastId=0", ct) as JsonArray;
            var allHud = await Get("hudmsg?lastEvt=0&lastDmg=0", ct);
            long MaxId(JsonArray? a) => a == null ? 0 : a.Where(x => x != null).Select(x => Long(x!, "id")).DefaultIfEmpty(0).Max();
            if (allChat != null && lc > 0 && MaxId(allChat) < lc) { _log.Write("ids-restarted", new { what = "chat", was = lc, now = MaxId(allChat) }); lc = 0; lock (_lock) _chatPrimed = false; }
            if (allHud != null)
            {
                var md = MaxId(allHud["damage"] as JsonArray); var me = MaxId(allHud["events"] as JsonArray);
                if (ld > 0 && md < ld) { _log.Write("ids-restarted", new { what = "damage", was = ld, now = md }); ld = 0; _dmgPrimed = false; }
                if (le > 0 && me < le) { _log.Write("ids-restarted", new { what = "events", was = le, now = me }); le = 0; }
            }
        }

        var hud = await Get($"hudmsg?lastEvt={le}&lastDmg={ld}", ct);
        if (hud?["damage"] is JsonArray dmg)
        {
            // the first answer after connecting holds the whole match so far: old deaths must not end the reward cycle
            var oldFeed = !_dmgPrimed;
            _dmgPrimed = true;
            foreach (var d in dmg)
            {
                if (d == null) continue;
                ld = Math.Max(ld, Long(d, "id"));
                if (!FirstTime("damage", d)) continue;
                fresh.Add(Item("damage", d)); _log.Write("damage", d);
                if (!oldFeed) Cycle.FeedLine(Str(d, "msg"), NowMs);
            }
        }
        if (hud?["events"] is JsonArray evs)
            foreach (var e in evs) if (e != null) { le = Math.Max(le, Long(e, "id")); _log.Write("hud-event", e); }

        var chat = await Get($"gamechat?lastId={lc}", ct);
        if (chat is JsonArray msgs)
        {
            bool backlog;
            lock (_lock) { backlog = !_chatPrimed; _chatPrimed = true; }
            foreach (var m in msgs)
            {
                if (m == null) continue;
                lc = Math.Max(lc, Long(m, "id"));
                if (!FirstTime("chat", m)) continue;
                var item = Item("chat", m) with { Backlog = backlog };
                fresh.Add(item);
                _log.Write(backlog ? "chat-backlog" : "chat", m);
                if (!item.Enemy && !backlog) _logMapNext = true;
            }
        }

        lock (_lock)
        {
            _lastEvt = le; _lastDmg = ld; _lastChat = lc;
            _events.AddRange(fresh);
            if (_events.Count > 400) _events.RemoveRange(0, _events.Count - 400);
        }
        if (fresh.Count > 0) _hub.Send("events", new { reset = false, items = fresh });
    }

    /// <summary>True the first time this match sees this message (same id, sender and text).</summary>
    private bool FirstTime(string kind, JsonNode n)
    {
        var key = $"{kind}|{Long(n, "id")}|{Str(n, "sender")}|{Str(n, "msg")}";
        lock (_lock)
        {
            if (_seenMsgs.Count > 5000) _seenMsgs.Clear();
            return _seenMsgs.Add(key);
        }
    }

    private EventItem Item(string kind, JsonNode n)
    {
        var msg = Str(n, "msg");
        var sender = Str(n, "sender");
        var own = _s.Callsign.Length > 0 && sender.Equals(_s.Callsign, StringComparison.OrdinalIgnoreCase);
        var me = own || (_s.Callsign.Length > 0 && msg.Contains(_s.Callsign, StringComparison.OrdinalIgnoreCase));
        return new EventItem(Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, msg,
            sender, Bool(n, "enemy"), Str(n, "mode"), me, own);
    }

    private async Task PollMission(CancellationToken ct)
    {
        var m = await Get("mission.json", ct);
        if (m == null) return;
        var text = m.ToJsonString();
        bool changed;
        lock (_lock) { changed = text != _missionText; if (changed) { _missionText = text; _mission = m; } }
        if (changed) { _hub.Send("mission", m); _log.Write("mission", m); }
    }

    // ------------------------------------------------------------------ derived values (called under _lock)
    private void Derive(bool swapped)
    {
        var type = Str(_ind!, "type");
        if (type.Length > 0 && type != _aircraft)
        {
            // new aircraft or respawn: start the session values over
            _aircraft = type;
            _log.Write("aircraft", new { type });
            _ui.SetAircraft(type);
            _hub.Send("ui", _ui.Snapshot());
            _gMin = _gMax = double.NaN;
            _fuel.Clear(); _burnKgMin = double.NaN;
        }

        var g = Num(_state!, "Ny");
        if (!double.IsNaN(g))
        {
            _gMin = double.IsNaN(_gMin) ? g : Math.Min(_gMin, g);
            _gMax = double.IsNaN(_gMax) ? g : Math.Max(_gMax, g);
        }

        var kg = Num(_state!, "Mfuel, kg");
        if (!double.IsNaN(kg))
        {
            var t = Environment.TickCount64 / 1000.0;
            if (_fuel.Count > 0 && kg > _fuel.Last().Kg + 1) _fuel.Clear(); // refuelled or respawned
            _fuel.Enqueue((t, kg));
            while (_fuel.Count > 0 && t - _fuel.Peek().T > 30) _fuel.Dequeue();
            var first = _fuel.Peek();
            var span = t - first.T;
            if (span >= 5) _burnKgMin = Math.Max(0, (first.Kg - kg) / span * 60);
        }

        Cycle.Observe(new RewardCycle.Sample(_aircraft, _inMatch, swapped, Num(_state!, "IAS, km/h"), Num(_state!, "RPM 1"),
            kg, Num(_state!, "Mfuel0, kg")), NowMs);
    }

    private object TeleObj()
    {
        var kg = Num(_state!, "Mfuel, kg");
        var max = Num(_state!, "Mfuel0, kg");
        double? endurance = null, toBingo = null;
        if (!double.IsNaN(_burnKgMin) && _burnKgMin > 0.05 && !double.IsNaN(kg))
        {
            endurance = kg / _burnKgMin * 60;
            toBingo = Math.Max(0, (kg - _ui.BingoKg) / _burnKgMin * 60);
        }
        return new
        {
            state = _state,
            ind = _ind,
            derived = new
            {
                aircraft = _aircraft,
                gMin = Nan(_gMin), gMax = Nan(_gMax),
                fuelKg = Nan(kg), fuelMaxKg = Nan(max),
                burnKgMin = Nan(_burnKgMin),
                enduranceS = endurance, toBingoS = toBingo,
            },
        };
    }

    private object StatusObj() => new { status = _status, aircraft = _aircraft, clients = _hub.Count, source = _s.WarThunderUrl, cycle = Cycle.Snapshot(NowMs) };

    // ------------------------------------------------------------------ helpers
    // the game writes NaN / infinity as bare words, which isn't JSON
    private static readonly System.Text.RegularExpressions.Regex BareNumbers =
        new(@"(?<=[:\[,]\s*)-?(?:nan|inf|infinity)(?=\s*[,\]}])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly Dictionary<string, DateTime> _lastGetProblem = new();

    private async Task<JsonNode?> Get(string path, CancellationToken ct)
    {
        string text;
        try { text = await _http.GetStringAsync(path, ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Problem(path, "http-error", new { path, e.Message });
            return null;
        }
        // map_obj.json answers with an empty body when the game shares no map objects (as since 9 Oct 2026): that is
        // "nothing on the map", not a broken answer
        if (string.IsNullOrWhiteSpace(text) && path.StartsWith("map_obj", StringComparison.Ordinal)) return new JsonArray();
        try { return JsonNode.Parse(BareNumbers.Replace(text, "null")); }
        catch (Exception e)
        {
            // keep the raw answer so a broken game response can be found in the match log
            Problem(path, "bad-json", new { path, e.Message, raw = text.Length > 8000 ? text[..8000] : text });
            return null;
        }
    }

    /// <summary>Log a failed request, at most every 30 s per endpoint (offline polling would otherwise flood the log).</summary>
    private void Problem(string path, string kind, object data)
    {
        var key = path.Split('?')[0];
        lock (_lock)
        {
            if (_lastGetProblem.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(30)) return;
            _lastGetProblem[key] = DateTime.UtcNow;
        }
        string status; lock (_lock) status = _status;
        if (status != "offline" || kind == "bad-json") _log.Write(kind, data);
    }

    private async Task<byte[]?> GetBytes(string path, CancellationToken ct)
    {
        try { var b = await _http.GetByteArrayAsync(path, ct); return b.Length > 0 ? b : null; }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    private static double? Nan(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : Math.Round(v, 3);

    public static double Num(JsonNode n, string key)
    {
        try { return n[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN; } catch { return double.NaN; }
    }
    private static long Long(JsonNode n, string key) { var d = Num(n, key); return double.IsNaN(d) ? 0 : (long)d; }
    private static bool Bool(JsonNode n, string key) { try { return n[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b; } catch { return false; } }
    private static string Str(JsonNode n, string key) { try { return n[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""; } catch { return ""; } }
}
