namespace WtMfd;

/// <summary>
/// What the MFD shows. Kept in the app (not the browser) so every screen stays in sync
/// and a reopened page comes back on the same page and zoom.
/// </summary>
public sealed class UiState
{
    public static readonly string[] Pages = { "flight", "engine", "map", "systems" };
    public static readonly string[] Palettes = { "green", "amber", "white" };
    public static readonly string[] Filters = { "all", "damage", "chat" };
    public const int MaxZoom = 10; // index into the zoom steps x1 ... x32 (ZOOMS in app.ts)

    private readonly object _lock = new();

    public string Page { get; private set; } = "flight";
    public int MapZoom { get; private set; }
    public bool MapFollow { get; private set; } = true;
    public bool MapGrid { get; private set; } = true;
    public bool MapTrail { get; private set; } = true;
    public bool MapMission { get; private set; } = true;
    public string Palette { get; private set; } = "green";
    public bool Night { get; private set; }
    public int Brightness { get; private set; } = 100;
    public bool Imperial { get; private set; }
    public string SpeedMode { get; private set; } = "IAS";
    public double BingoKg { get; private set; }
    public string EventFilter { get; private set; } = "all";
    public bool InvertAdiPitch { get; }
    public bool InvertAdiRoll { get; }

    private readonly List<WeaponProfile> _weapons;
    private string _aircraft = "";
    private string _weapon = ""; // selected profile name, "" = off

    public UiState(Settings s)
    {
        _weapons = s.Weapons ?? new();
        BingoKg = s.DefaultBingoKg;
        InvertAdiPitch = s.InvertAdiPitch;
        InvertAdiRoll = s.InvertAdiRoll;
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                Page, MapZoom, MapFollow, MapGrid, MapTrail, MapMission, Palette, Night, Brightness, Imperial, SpeedMode, BingoKg,
                EventFilter, InvertAdiPitch, InvertAdiRoll, Osb = OsbLabels(),
                Waypoints = _wps.Select(w => new { x = w.X, y = w.Y }).ToArray(),
                Target = Target is { } t ? new { x = t.X, y = t.Y, icon = t.Icon } : null,
                Weapon = SelectedWeapon() is { } w ? new { name = w.Name, avgSpeed = w.AvgSpeed, pitchOffset = w.PitchOffset, maxRange = w.MaxRange } : null,
            };
        }
    }

    /// <summary>The labels the MFD prints next to its buttons. Bottom row changes with the page, like a real MFD.</summary>
    private Labels OsbLabels()
    {
        // Top button 1 is left blank so the Stream Deck key above it can be the folder "back" key.
        var top = new[] { "", "FLT", "ENG", "MAP", "SYS" };
        string[] bottom = Page switch
        {
            "flight" => new[] { "G RST", SpeedMode == "IAS" ? "TAS" : "IAS", "CYC RST", "", "" },
            "engine" => new[] { "BINGO-", "BINGO+", "BURN RST", "", "" },
            "map" => new[] { "ZOOM-", "ZOOM+", MapFollow ? "FULL" : "FOLLOW", MapMission ? "MSN OFF" : "MSN ON", MapTrail ? "TRAIL OFF" : "TRAIL ON" },
            "systems" => new[] { "FILTER", "CLEAR", "", "", "" },
            _ => new[] { "", "", "", "", "" },
        };
        // Third row: display settings only on SYS; on the other pages it holds more of that page's controls.
        string[] global = Page switch
        {
            "flight" => new[] { "NEXT TGT", Target != null ? "CLR TGT" : "", WeaponLabel(), "", "" },
            "engine" => new[] { "BINGO -500", "BINGO +500", "BINGO 25%", "", "" },
            "map" => new[] { _wps.Count > 0 ? "CLR WP" : "", _wps.Count > 0 ? "DEL WP" : "", "NEXT TGT", Target != null ? "CLR TGT" : "", MapGrid ? "GRID OFF" : "GRID ON" },
            _ => new[] { Night ? "DAY" : "NIGHT", "DIM", "BRT", "COLOR", Imperial ? "METRIC" : "IMPERIAL" },
        };
        return new Labels(top, bottom, global);
    }

    public record Labels(string[] Top, string[] Bottom, string[] Global);

    /// <summary>Short text for a Stream Deck key after it was pressed: the button's current label, or OK.</summary>
    public string LabelFor(string cmd)
    {
        cmd = cmd.Trim('/').ToLowerInvariant();
        if (!cmd.StartsWith("osb/") || cmd.Length != 6 || !int.TryParse(cmd[5..], out var n) || n < 1 || n > 5) return "OK";
        lock (_lock)
        {
            var l = OsbLabels();
            var row = cmd[4] switch { 't' => l.Top, 'b' => l.Bottom, 'g' => l.Global, _ => null };
            return row?[n - 1] ?? "OK";
        }
    }

    // ---------- mutations (all under the lock) ----------
    public void SetPage(string p) { lock (_lock) if (Pages.Contains(p)) Page = p; }
    public void NextPage(int dir)
    {
        lock (_lock)
        {
            var i = Array.IndexOf(Pages, Page);
            Page = Pages[((i + dir) % Pages.Length + Pages.Length) % Pages.Length];
        }
    }
    public void Zoom(int d) { lock (_lock) MapZoom = Math.Clamp(MapZoom + d, 0, MaxZoom); }
    public void ToggleFollow() { lock (_lock) MapFollow = !MapFollow; }
    public void ToggleGrid() { lock (_lock) MapGrid = !MapGrid; }
    public void ToggleTrail() { lock (_lock) MapTrail = !MapTrail; }
    public void ToggleMission() { lock (_lock) MapMission = !MapMission; }
    public void ToggleNight() { lock (_lock) Night = !Night; }
    public void Bright(int d) { lock (_lock) Brightness = Math.Clamp(Brightness + d, 20, 100); }
    public void NextPalette() { lock (_lock) Palette = Palettes[(Array.IndexOf(Palettes, Palette) + 1) % Palettes.Length]; }
    public void ToggleUnits() { lock (_lock) Imperial = !Imperial; }
    public void ToggleSpeed() { lock (_lock) SpeedMode = SpeedMode == "IAS" ? "TAS" : "IAS"; }
    public void Bingo(double d) { lock (_lock) BingoKg = Math.Clamp(BingoKg + d, 0, 20000); }
    public void SetBingo(double kg) { lock (_lock) BingoKg = Math.Clamp(Math.Round(kg / 10) * 10, 0, 20000); }
    public void NextFilter() { lock (_lock) EventFilter = Filters[(Array.IndexOf(Filters, EventFilter) + 1) % Filters.Length]; }

    // ------------------------------------------------------------------ waypoints (clicked on the map)
    // Positions are map coordinates (0..1 across the map, y down), the same as the game's map objects.
    public const int MaxWaypoints = 9;
    public readonly record struct Waypoint(double X, double Y);
    private readonly List<Waypoint> _wps = new();

    public void AddWaypoint(double x, double y)
    {
        lock (_lock) if (_wps.Count < MaxWaypoints) _wps.Add(new Waypoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
    }
    /// <summary>Remove waypoint n (1 = the active one).</summary>
    public void RemoveWaypoint(int n) { lock (_lock) if (n >= 1 && n <= _wps.Count) _wps.RemoveAt(n - 1); }
    public void NextWaypoint() { lock (_lock) if (_wps.Count > 0) _wps.RemoveAt(0); }
    public bool ClearWaypoints() { lock (_lock) { var any = _wps.Count > 0; _wps.Clear(); return any; } }
    public Waypoint? ActiveWaypoint { get { lock (_lock) return _wps.Count > 0 ? _wps[0] : null; } }

    // ------------------------------------------------------------------ weapon profile for the hold-over estimate
    private List<WeaponProfile> ProfilesFor(string aircraft) =>
        _weapons.Where(w => aircraft.StartsWith(w.Aircraft, StringComparison.OrdinalIgnoreCase)).ToList();
    private WeaponProfile? SelectedWeapon() =>
        _weapon.Length == 0 ? null : ProfilesFor(_aircraft).FirstOrDefault(w => w.Name == _weapon);
    private string WeaponLabel()
    {
        var list = ProfilesFor(_aircraft);
        if (list.Count == 0) return "";
        return _weapon.Length > 0 ? _weapon : "WPN OFF";
    }
    /// <summary>The aircraft changed (spawn, respawn): select its first weapon profile, if it has one.</summary>
    public void SetAircraft(string aircraft)
    {
        lock (_lock)
        {
            if (aircraft == _aircraft) return;
            _aircraft = aircraft;
            _weapon = ProfilesFor(aircraft).FirstOrDefault()?.Name ?? "";
        }
    }
    /// <summary>Cycle this aircraft's profiles, then off.</summary>
    public void NextWeapon()
    {
        lock (_lock)
        {
            var names = ProfilesFor(_aircraft).Select(w => w.Name).Append("").ToList();
            _weapon = names[(names.IndexOf(_weapon) + 1) % names.Count];
        }
    }

    // ------------------------------------------------------------------ target (NEXT TGT): one enemy object, not a route
    public readonly record struct TargetMark(double X, double Y, string Icon);
    public TargetMark? Target { get; private set; }
    public void SetTarget(TargetMark? t) { lock (_lock) Target = t; }
}
