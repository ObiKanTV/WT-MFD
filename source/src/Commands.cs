namespace WtMfd;

/// <summary>
/// Everything the Stream Deck can do. Each command is a URL: http://localhost:8120/api/cmd/&lt;command&gt;.
/// The osb/* commands behave like real MFD buttons: what the bottom row does depends on the page.
/// </summary>
public static class Commands
{
    public static readonly (string Cmd, string What)[] List =
    {
        ("osb/t1", "Top button 1: unused (Stream Deck back key)"),
        ("osb/t2", "Top button 2: Flight page"),
        ("osb/t3", "Top button 3: Engine & fuel page"),
        ("osb/t4", "Top button 4: Map page"),
        ("osb/t5", "Top button 5: Systems & events page"),
        ("osb/b1", "Bottom button 1 (depends on page: G reset / bingo - / zoom out / event filter)"),
        ("osb/b2", "Bottom button 2 (depends on page: IAS-TAS / bingo + / zoom in / clear events)"),
        ("osb/b3", "Bottom button 3 (depends on page: restart reward cycle / reset burn rate / follow-full map / -)"),
        ("osb/b4", "Bottom button 4 (map: mission objectives on/off)"),
        ("osb/b5", "Bottom button 5 (map: trail on/off)"),
        ("osb/g1", "Global 1: night mode on/off"),
        ("osb/g2", "Global 2: dimmer"),
        ("osb/g3", "Global 3: brighter"),
        ("osb/g4", "Global 4: next colour (green, amber, white)"),
        ("osb/g5", "Global 5: metric / imperial units"),
        ("page/flight", "Flight page"), ("page/engine", "Engine & fuel page"), ("page/map", "Map page"), ("page/systems", "Systems & events page"),
        ("page/next", "Next page"), ("page/prev", "Previous page"),
        ("map/zoomin", "Map zoom in"), ("map/zoomout", "Map zoom out"), ("map/follow", "Map: follow me / whole map"),
        ("map/grid", "Map grid on/off"), ("map/trail", "Map trail on/off"), ("map/mission", "Mission objectives on the map on/off"),
        ("fuel/bingo-up", "Bingo +100 kg"), ("fuel/bingo-down", "Bingo -100 kg"), ("fuel/reset", "Restart the burn-rate average"),
        ("g/reset", "Reset G min/max"), ("speed/toggle", "Show IAS or TAS"),
        ("events/filter", "Event log: all / damage / chat"), ("events/clear", "Clear the event log"),
        ("display/night", "Night mode on/off"), ("display/dim", "Dimmer"), ("display/bright", "Brighter"),
        ("display/color", "Next colour"), ("display/units", "Metric / imperial"),
        ("wp/next", "Waypoints: remove the active one"), ("wp/clear", "Waypoints: remove all"),
        ("tgt/next", "Target: select the next enemy target (nearest first)"), ("tgt/clear", "Target: deselect"),
        ("wpn/next", "Hold-over estimate: next weapon profile for this aircraft, or off"),
        ("fuel/bingo-up-500", "Bingo +500 kg"), ("fuel/bingo-down-500", "Bingo -500 kg"), ("fuel/bingo-25", "Bingo = 25% of a full tank"),
        ("cycle/reset", "Air Sim reward cycle: start a fresh 15-minute cycle now (it also starts by itself when you spawn)"),
        ("cycle/stop", "Air Sim reward cycle: switch the timer off until the next spawn"),
    };

    public static bool Apply(string cmd, UiState ui, Telemetry tel)
    {
        cmd = cmd.Trim('/').ToLowerInvariant();
        if (cmd.StartsWith("osb/")) cmd = Osb(cmd[4..], ui);
        if (cmd == "") return true; // an unlabeled button on this page: harmless no-op

        // waypoints from map clicks: wp/add/<x>/<y> (map coordinates 0..1), wp/del/<n>
        var parts = cmd.Split('/');
        if (parts.Length == 4 && parts[0] == "wp" && parts[1] == "add"
            && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wx)
            && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wy))
        {
            ui.AddWaypoint(wx, wy);
            return true;
        }
        // manual target from Ctrl+click on the map: tgt/set/<x>/<y>[/<icon>] (icon = the map object it was put on, so it follows it)
        if ((parts.Length == 4 || parts.Length == 5) && parts[0] == "tgt" && parts[1] == "set"
            && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tx)
            && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ty)
            && tx >= 0 && tx <= 1 && ty >= 0 && ty <= 1)
        {
            var icon = parts.Length == 5 ? Uri.UnescapeDataString(parts[4]) : "manual";
            ui.SetTarget(new UiState.TargetMark(tx, ty, icon.Length is > 0 and <= 40 ? icon : "manual"));
            return true;
        }
        if (parts.Length == 3 && parts[0] == "wp" && parts[1] == "del" && int.TryParse(parts[2], out var n))
        {
            ui.RemoveWaypoint(n);
            return true;
        }

        switch (cmd)
        {
            case "page/flight": ui.SetPage("flight"); break;
            case "page/engine": ui.SetPage("engine"); break;
            case "page/map": ui.SetPage("map"); break;
            case "page/systems": ui.SetPage("systems"); break;
            case "page/next": ui.NextPage(1); break;
            case "page/prev": ui.NextPage(-1); break;
            case "map/zoomin": ui.Zoom(1); break;
            case "map/zoomout": ui.Zoom(-1); break;
            case "map/follow": ui.ToggleFollow(); break;
            case "map/grid": ui.ToggleGrid(); break;
            case "map/trail": ui.ToggleTrail(); break;
            case "map/mission": ui.ToggleMission(); break;
            case "fuel/bingo-up": ui.Bingo(100); break;
            case "fuel/bingo-down": ui.Bingo(-100); break;
            case "fuel/reset": tel.ResetBurn(); break;
            case "g/reset": tel.ResetG(); break;
            case "speed/toggle": ui.ToggleSpeed(); break;
            case "events/filter": ui.NextFilter(); break;
            case "events/clear": tel.ClearEvents(); break;
            case "display/night": ui.ToggleNight(); break;
            case "display/dim": ui.Bright(-10); break;
            case "display/bright": ui.Bright(10); break;
            case "display/color": ui.NextPalette(); break;
            case "display/units": ui.ToggleUnits(); break;
            case "wp/next": ui.NextWaypoint(); break;
            case "wp/clear": ui.ClearWaypoints(); break;
            case "tgt/next": tel.NextTarget(); break;
            case "tgt/clear": ui.SetTarget(null); break;
            case "wpn/next": ui.NextWeapon(); break;
            case "fuel/bingo-up-500": ui.Bingo(500); break;
            case "fuel/bingo-down-500": ui.Bingo(-500); break;
            case "fuel/bingo-25": tel.BingoFraction(0.25); break;
            case "cycle/reset": tel.ResetCycle(); break;
            case "cycle/stop": tel.StopCycle(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>Translates a physical button (top/bottom/global row, 1-5) into a command for the current page.</summary>
    private static string Osb(string key, UiState ui)
    {
        var page = ui.Page;
        return key switch
        {
            "t1" => "",                // left free for the Stream Deck folder back key
            "t2" => "page/flight",
            "t3" => "page/engine",
            "t4" => "page/map",
            "t5" => "page/systems",
            "g1" => page switch { "flight" => "tgt/next", "engine" => "fuel/bingo-down-500", "map" => "wp/clear", "systems" => "display/night", _ => "" },
            "g2" => page switch { "flight" => "tgt/clear", "engine" => "fuel/bingo-up-500", "map" => "wp/next", "systems" => "display/dim", _ => "" },
            "g3" => page switch { "flight" => "wpn/next", "engine" => "fuel/bingo-25", "map" => "tgt/next", "systems" => "display/bright", _ => "" },
            "g4" => page switch { "map" => "tgt/clear", "systems" => "display/color", _ => "" },
            "g5" => page switch { "map" => "map/grid", "systems" => "display/units", _ => "" },
            "b1" => page switch { "flight" => "g/reset", "engine" => "fuel/bingo-down", "map" => "map/zoomout", "systems" => "events/filter", _ => "" },
            "b2" => page switch { "flight" => "speed/toggle", "engine" => "fuel/bingo-up", "map" => "map/zoomin", "systems" => "events/clear", _ => "" },
            "b3" => page switch { "flight" => "cycle/reset", "engine" => "fuel/reset", "map" => "map/follow", _ => "" },
            "b4" => page switch { "map" => "map/mission", _ => "" },
            "b5" => page switch { "map" => "map/trail", _ => "" },
            _ => "unknown",
        };
    }
}
