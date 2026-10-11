using System.Text.RegularExpressions;

namespace WtMfd;

/// <summary>
/// The Air Simulator (Enduring Confrontation) reward cycle.
///
/// Sim pays out in windows of 15 minutes that start when you spawn: at the end of each window your score in it sets
/// the reward ("Useful actions" on screen), and landing after that message pays the last 20%. Dying ends the window
/// and the next spawn starts a fresh one. Landing to repair and rearm does NOT restart it.
///
/// War Thunder doesn't publish any of this, so the cycle is worked out from what it does publish:
/// <list type="bullet">
/// <item>Death: the kill feed names you ("... shot down &lt;Callsign&gt; (AJS37)", "&lt;Callsign&gt; (AJS37) has crashed.").</item>
/// <item>Spawn: the game swaps the vehicle (a split-second "hangar" blip, a new aircraft type, or, after a death, a full
///   fuel load in one step) and the engine then spools up from cold. The respawn screen shows the same kind of vehicle as a real spawn,
///   but its engine never runs, so a swap only counts once the engine starts (or the aircraft moves). The cycle start
///   is dated back to the swap.</item>
/// <item>Landing: stopped on the ground with the engine running, after having flown.</item>
/// </list>
/// Everything here is a best guess from real match logs; the CYC RST button restarts the cycle by hand.
/// All methods take the time as unix milliseconds so the logic can be replayed against recorded matches.
/// </summary>
public sealed class RewardCycle
{
    public readonly record struct Sample(string Aircraft, bool InMatch, bool Swapped, double Ias, double Rpm, double FuelKg, double FuelMax);

    private readonly object _lock = new();
    private readonly Action<string, object> _log;
    private readonly long _cycleMs;
    private readonly Regex? _death, _kill;

    private string _state = "off";          // off | running | down
    private string _via = "";               // spawn | manual
    private long _start;                    // cycle 1 started here (unix ms)
    private int _paid;                      // cycles completed this life
    private bool _bonusDue;                 // a cycle completed since the last landing: landing now pays the last 20%
    private long _bonusDueAt, _bonusTakenAt = -1;
    private readonly List<long> _kills = new();

    // spawn detection
    private long _pendingAt = -1;           // a vehicle swap was seen here; it becomes the spawn once the aircraft shows life
    private bool _pendingFuelOnly;          // the swap was only a fuel jump: needs a real cold engine start (rearming refuels too)
    private bool _pendingSawCold;
    private string _pendingWhy = "";

    private long _swapAt = -1;              // the game just swapped the vehicle (waiting for the new one's first real numbers)
    private string _swapWhy = "";
    private double _swapIasBefore = double.NaN;

    // last sample
    private long _lastAt = -1;
    private string _lastAircraft = "";
    private double _lastIas = double.NaN, _lastFuel = double.NaN;

    // landing detection
    private bool _airborne;
    private long _stoppedSince = -1;

    public RewardCycle(Settings s, Action<string, object> log)
    {
        _log = log;
        _cycleMs = (long)(Math.Clamp(s.CycleMinutes, 0.1, 120) * 60_000);
        var cs = (s.Callsign ?? "").Trim();
        if (cs.Length > 0)
        {
            var me = Regex.Escape(cs);
            // "<who> shot down <me> (AJS37)", "<who> destroyed [CLAN] <me> (AJS37)", "<me> (AJS37) has crashed."
            _death = new Regex($@"(?:(?:shot down|destroyed) (?:\S+ )?{me} \()|(?:(?:^|\s){me} \(.*\) has crashed)", RegexOptions.Compiled);
            // "<me> (AJS37) shot down X", "<me> (AJS37) destroyed [ai] X" (a missile shot out of the air doesn't count)
            _kill = new Regex($@"(?:^|\s){me} \(.*\) (?:shot down|destroyed) (?!weapons/)", RegexOptions.Compiled);
        }
    }

    public bool KnowsCallsign => _death != null;
    public double CycleSeconds => _cycleMs / 1000.0;

    // ------------------------------------------------------------------ inputs

    /// <summary>One flight data sample (about 10 per second while the game reports an aircraft).</summary>
    public void Observe(Sample s, long now)
    {
        lock (_lock)
        {
            Tick(now);
            var real = s.Aircraft.Length > 0 && s.Aircraft != "dummy_plane";
            var fresh = _lastAt > 0 && now - _lastAt <= 1500;

            // ---- a vehicle swap: the start of a spawn, or the respawn screen.
            // The first sample after a swap can still carry the old vehicle's numbers (logs show a stale 300 km/h dummy
            // right after the aircraft changes), so a swap opens a short window and counts once the new vehicle stands still.
            if (real && s.Aircraft != _lastAircraft) OpenSwap(now, _lastAircraft is "" or "dummy_plane" ? "first aircraft" : "new aircraft");
            else if (real && s.Swapped) OpenSwap(now, "vehicle swap");
            if (_swapAt >= 0)
            {
                if (real && s.Ias < 60)
                {
                    // a swap straight out of fast flight means you left that aircraft (J-out, or a death the feed missed)
                    if (_state == "running" && _swapIasBefore > 150) Down(now, "left the aircraft");
                    Pend(_swapAt, _swapWhy, fuelOnly: false);
                    _swapAt = -1;
                }
                else if (now - _swapAt > 10_000)
                {
                    // WT-MFD started (or the match loaded) while you were already flying: the spawn time is unknown, CYC RST it
                    if (_swapWhy == "first aircraft" && real) _log("cycle", new { evt = "joined mid-flight", aircraft = s.Aircraft });
                    _swapAt = -1;
                }
            }
            // a full tank in one step after a death is a respawn; while alive it is the ground crew rearming you
            // (logs show it at every landing: engine off, fuel topped up in two steps), which keeps the cycle going
            else if (real && s.Ias < 60 && _state != "running" && fresh && !double.IsNaN(_lastFuel) && s.FuelMax > 0
                     && s.FuelKg - _lastFuel >= Math.Max(60, 0.05 * s.FuelMax))
                Pend(now, "fuel reload", fuelOnly: true);

            // ---- the pending swap turns into a spawn when the aircraft comes alive
            if (_pendingAt >= 0)
            {
                if (!real || !s.InMatch) { /* still on the respawn screen or loading */ }
                else
                {
                    if (s.Rpm < 100) _pendingSawCold = true;
                    // after a confirmed death anything that runs is the new aircraft; otherwise (rearming, start-up) the engine
                    // must start from cold, because the ground crew refuels with the engine running too
                    var dead = _state == "down";
                    var engineStarted = (_pendingSawCold || dead) && s.Rpm > 1000;
                    var moving = s.Ias > 30 && (dead || !_pendingFuelOnly);
                    if (engineStarted || moving)
                    {
                        Start(_pendingAt, "spawn", $"{_pendingWhy}, then {(engineStarted ? "engine start" : "moving")}");
                        _pendingAt = -1;
                    }
                }
                // a pending swap that never comes alive is forgotten after 10 minutes
                if (_pendingAt >= 0 && now - _pendingAt > 600_000) _pendingAt = -1;
            }

            // ---- landing: stopped on the ground with the engine running, after having flown
            if (real && _state == "running")
            {
                if (s.Ias > 150) { _airborne = true; _stoppedSince = -1; }
                else if (_airborne && s.Ias < 15 && s.Rpm > 500)
                {
                    if (_stoppedSince < 0) _stoppedSince = now;
                    else if (now - _stoppedSince >= 4000) { _airborne = false; _stoppedSince = -1; Landed(now); }
                }
                else _stoppedSince = -1;
            }

            if (real || s.Aircraft == "dummy_plane") _lastAircraft = s.Aircraft;
            _lastAt = now; _lastIas = s.Ias; _lastFuel = s.FuelKg;
        }
    }

    /// <summary>A fresh line from the kill feed.</summary>
    public void FeedLine(string msg, long now)
    {
        if (_death == null || string.IsNullOrEmpty(msg)) return;
        lock (_lock)
        {
            Tick(now);
            if (_death.IsMatch(msg)) { if (_state == "running") Down(now, msg); _pendingAt = -1; }
            else if (_kill!.IsMatch(msg) && _state == "running") { _kills.Add(now); _log("cycle", new { evt = "kill", msg }); }
        }
    }

    /// <summary>Left the battle (back in the hangar between matches) or the game closed.</summary>
    public void Stop(string why, long now)
    {
        lock (_lock)
        {
            _pendingAt = -1; _swapAt = -1; _lastAircraft = ""; _lastIas = double.NaN; _lastFuel = double.NaN;
            if (_state == "off") return;
            _state = "off"; _bonusDue = false;
            _log("cycle", new { evt = "off", why });
        }
    }

    /// <summary>CYC RST: start a fresh cycle right now.</summary>
    public void Reset(long now) { lock (_lock) { _pendingAt = -1; Start(now, "manual", "CYC RST"); } }

    // ------------------------------------------------------------------ state changes (under the lock)

    private void OpenSwap(long now, string why)
    {
        // the latest swap is the best spawn time; the speed before the FIRST one says whether you were flying
        if (_swapAt < 0) { _swapIasBefore = _lastIas; _swapWhy = why; }
        else if (why != "vehicle swap") _swapWhy = why; // an aircraft change tells more than the blip that came with it
        _swapAt = now;
    }

    private void Pend(long at, string why, bool fuelOnly)
    {
        // a fuel jump never replaces a real swap that is still waiting: the swap is the better spawn time
        if (_pendingAt >= 0 && fuelOnly && !_pendingFuelOnly) return;
        _pendingAt = at; _pendingFuelOnly = fuelOnly; _pendingSawCold = false; _pendingWhy = why;
    }

    private void Start(long at, string via, string why)
    {
        _state = "running"; _via = via; _start = at; _paid = 0; _bonusDue = false; _bonusTakenAt = -1;
        _kills.Clear(); _airborne = false; _stoppedSince = -1;
        _log("cycle", new { evt = "start", via, why, at = DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("HH:mm:ss.fff") });
    }

    private void Down(long now, string why)
    {
        if (_state != "running") return;
        var lost = _bonusDue;
        _state = "down"; _bonusDue = false; _start = now;
        _log("cycle", new { evt = "down", why, cyclesPaid = _paid, landingBonusLost = lost });
    }

    private void Landed(long now)
    {
        if (_bonusDue) { _bonusDue = false; _bonusTakenAt = now; }
        _log("cycle", new { evt = "landed", bonus = _bonusTakenAt == now });
    }

    /// <summary>Cross into the next cycle when its time is up: that one has paid out, so landing is now worth the bonus.</summary>
    private void Tick(long now)
    {
        if (_state != "running") return;
        var done = (int)((now - _start) / _cycleMs);
        if (done > _paid)
        {
            _paid = done; _bonusDue = true; _bonusDueAt = _start + done * _cycleMs;
            _kills.RemoveAll(k => k < _bonusDueAt);
            _log("cycle", new { evt = "paid", cycle = done });
        }
    }

    // ------------------------------------------------------------------ output

    /// <summary>For the MFD and the Stream Deck, sent with every status message (once a second).</summary>
    public object Snapshot(long now)
    {
        lock (_lock)
        {
            Tick(now);
            var running = _state == "running";
            var elapsed = Math.Max(0, now - _start);
            var cycleStart = _start + _paid * _cycleMs;
            return new
            {
                state = _pendingAt >= 0 && _state != "running" ? "spawning" : _state,
                n = running ? _paid + 1 : 0,
                leftS = running ? Math.Max(0, (cycleStart + _cycleMs - now) / 1000.0) : (double?)null,
                lenS = _cycleMs / 1000.0,
                elapsedS = _state == "off" ? (double?)null : elapsed / 1000.0,
                kills = running ? _kills.Count(k => k >= cycleStart) : 0,
                bonus = _bonusDue ? "due" : _bonusTakenAt >= 0 && now - _bonusTakenAt < 20_000 ? "taken" : "",
                bonusDueS = _bonusDue ? (now - _bonusDueAt) / 1000.0 : (double?)null,
                via = _via,
                feed = _death != null,
            };
        }
    }
}
