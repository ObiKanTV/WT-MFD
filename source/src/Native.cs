using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WtMfd;

[SupportedOSPlatform("windows")]
internal static class Native
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    /// <summary>WT-MFD.exe is a windowed program; with --console from a command prompt, write to that prompt.</summary>
    public static void AttachToParentConsole()
    {
        try { AttachConsole(-1); } catch { }
    }

    private static string Dir => AppContext.BaseDirectory;
    private static string Pending => Path.Combine(Dir, "WT-MFD.dll.new");
    public static bool UpdatePending => File.Exists(Pending);

    /// <summary>
    /// Updates arrive as WT-MFD.dll.new, because the running program locks WT-MFD.dll. A hidden helper waits until
    /// this process has really ended, swaps the file in (retrying for a few seconds in case something still holds it),
    /// and, when <paramref name="restart"/> is set, starts WT-MFD again.
    /// </summary>
    public static void ApplyPendingUpdate(bool restart)
    {
        try
        {
            if (!UpdatePending) return;
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";
            var target = Path.Combine(Dir, "WT-MFD.dll");
            var host = Environment.ProcessPath ?? "";
            var start = !restart ? ""
                : host.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                    ? $"Start-Process -FilePath {Q(host)} -ArgumentList {Q("\"" + target + "\" --updated")} -WorkingDirectory {Q(Dir)};"
                    : $"Start-Process -FilePath {Q(host)} -ArgumentList '--updated' -WorkingDirectory {Q(Dir)};";
            var script =
                $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue;" +
                $"for ($i = 0; $i -lt 40; $i++) {{ try {{ Move-Item -LiteralPath {Q(Pending)} -Destination {Q(target)} -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Milliseconds 250 }} }};" +
                start;
            var psi = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Dir };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", script })
                psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { /* Start MFD.cmd swaps it in on the next start instead */ }
    }
}
