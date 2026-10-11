using System.Text.Json;

namespace WtMfd;

/// <summary>Settings from settings.json next to the exe, overridable on the command line (--port 8120 --wt http://localhost:8111 --lan).</summary>
public sealed class Settings
{
    public int Port { get; set; } = 8120;
    public string WarThunderUrl { get; set; } = "http://localhost:8111";
    /// <summary>Listen on the whole network (for a tablet). Off by default: only this PC can reach the MFD.</summary>
    public bool Lan { get; set; }
    /// <summary>Your in-game name. Lines in the event log that mention it are highlighted.</summary>
    public string Callsign { get; set; } = "";
    public double DefaultBingoKg { get; set; } = 1000;
    public bool InvertAdiPitch { get; set; }
    public bool InvertAdiRoll { get; set; }
    /// <summary>Record what the game sends, one file per match, in logs\ next to the app.</summary>
    public bool Log { get; set; } = true;
    /// <summary>Open the MFD in its own app window (Windows). false = console + browser, like before.</summary>
    public bool Window { get; set; } = true;
    /// <summary>Length of the Air Simulator reward cycle (Enduring Confrontation pays out every 15 minutes).</summary>
    public double CycleMinutes { get; set; } = 15;

    /// <summary>
    /// Unguided weapons the FLT page can work out a hold-over for. The game doesn't say which weapon is selected, so
    /// profiles are picked by aircraft (Aircraft matches the start of the game's aircraft name) and cycled with a button.
    /// AvgSpeed is the average speed over the flight (m/s), PitchOffset corrects for how the launchers sit on the airframe.
    /// Tune both after a test flight: if salvos land short, lower AvgSpeed or raise PitchOffset.
    /// </summary>
    public List<WeaponProfile> Weapons { get; set; } = new()
    {
        new("mi_28", "S-8KO", 650, -1.8, 6000),   // calibrated in game by Kai: 650 m/s, launchers sit ~1.8° above the pitch reference
    };

    public static Settings Load(string[] args)
    {
        var s = new Settings();
        var file = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (File.Exists(file))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (loaded != null) s = loaded;
            }
            catch (Exception e)
            {
                Console.WriteLine($"settings.json could not be read ({e.Message}). Using defaults.");
            }
        }
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i].ToLowerInvariant())
            {
                case "--port": if (int.TryParse(Next(), out var p)) s.Port = p; break;
                case "--wt": s.WarThunderUrl = Next(); break;
                case "--lan": s.Lan = true; break;
                case "--callsign": s.Callsign = Next(); break;
                case "--nolog": s.Log = false; break;
                case "--console": s.Window = false; break;
            }
        }
        return s;
    }
}

/// <param name="MaxRange">Beyond this (metres) the FLT page says OUT OF RANGE instead of a hold.</param>
public sealed record WeaponProfile(string Aircraft, string Name, double AvgSpeed, double PitchOffset, double MaxRange = 6000);
