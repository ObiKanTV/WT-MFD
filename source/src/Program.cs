using System.Reflection;
using WtMfd;

var settings = Settings.Load(args);

// As an app (default on Windows): the MFD opens in its own borderless window and no console is shown.
// --console (or "Window": false in settings.json) runs it the old way: a console window, MFD in any browser.
var windowed = OperatingSystem.IsWindows() && settings.Window && !args.Contains("--console", StringComparer.OrdinalIgnoreCase);
if (windowed && OperatingSystem.IsWindows() && MfdWindow.BringExistingToFront()) return; // already running: show that one instead of starting a second
// An update is waiting (it arrived while WT-MFD was running): let the helper swap it in and start us again.
// --updated marks that restart, so a swap that couldn't happen never turns into a restart loop.
if (OperatingSystem.IsWindows() && Native.UpdatePending && !args.Contains("--updated", StringComparer.OrdinalIgnoreCase))
{
    Native.ApplyPendingUpdate(restart: true);
    return;
}
if (!windowed && OperatingSystem.IsWindows()) Native.AttachToParentConsole(); // the exe has no console of its own

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
// don't wait for open live streams (the Stream Deck keeps one open) when closing: quit within 2 seconds
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
// "localhost" binds both 127.0.0.1 and ::1. Windows resolves localhost to ::1 first, and a port that only
// listens on 127.0.0.1 makes every Stream Deck request wait ~2 s for the IPv6 attempt to fail.
builder.WebHost.UseUrls(settings.Lan ? $"http://*:{settings.Port}" : $"http://localhost:{settings.Port}");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Hub>();
builder.Services.AddSingleton<UiState>();
builder.Services.AddSingleton<MatchLog>();
builder.Services.AddSingleton<Telemetry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Telemetry>());

var app = builder.Build();
var asm = Assembly.GetExecutingAssembly();

// ---------------------------------------------------------------- the MFD page (compiled into the exe)
IResult Resource(string name)
{
    var stream = asm.GetManifestResourceStream("web/" + name);
    if (stream == null) return Results.NotFound();
    var type = Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
    return Results.Stream(stream, type);
}
app.MapGet("/", () => Resource("index.html"));
app.MapGet("/{file:regex(^[a-z0-9_.-]+$)}", (string file) => Resource(file));

// ---------------------------------------------------------------- live data stream
app.MapGet("/api/stream", async (HttpContext ctx, Hub hub, Telemetry tel, string? only) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    // ?only=ui is the light feed the Stream Deck plugin uses: button labels and link status, no flight data.
    // The plugin runs inside the Stream Deck app (a different origin), so this feed allows cross-origin reads.
    var uiOnly = only == "ui";
    if (uiOnly) ctx.Response.Headers.AccessControlAllowOrigin = "*";
    bool Wanted(string m) => !uiOnly || m.StartsWith("{\"type\":\"ui\"") || m.StartsWith("{\"type\":\"status\"");
    var (id, reader) = hub.Join();
    var ct = ctx.RequestAborted;
    try
    {
        foreach (var m in tel.Snapshot()) if (Wanted(m)) await ctx.Response.WriteAsync($"data: {m}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var m)) if (Wanted(m)) await ctx.Response.WriteAsync($"data: {m}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally { hub.Leave(id); }
});

app.MapGet("/api/map.img", (Telemetry tel) =>
{
    var (bytes, type) = tel.MapImage();
    return bytes == null ? Results.NotFound() : Results.File(bytes, type);
});

// ---------------------------------------------------------------- Stream Deck commands (GET or POST)
IResult RunCommand(string cmd, UiState ui, Telemetry tel, Hub hub)
{
    if (!Commands.Apply(cmd, ui, tel)) return Results.NotFound(new { error = "unknown command", cmd, see = "/api/commands" });
    var snap = ui.Snapshot();
    hub.Send("ui", snap);
    return Results.Ok(snap);
}
// Stream Deck (GET): reply with a short plain-text label so a key that shows the reply stays readable.
// Add ?json=1 to get the full state instead. The MFD page itself uses POST and gets JSON.
app.MapGet("/api/cmd/{**cmd}", (string cmd, string? json, UiState ui, Telemetry tel, Hub hub) =>
{
    var result = RunCommand(cmd, ui, tel, hub);
    if (json != null || result is not Microsoft.AspNetCore.Http.HttpResults.Ok<object>) return result;
    return Results.Text(ui.LabelFor(cmd), "text/plain; charset=utf-8");
});
app.MapPost("/api/cmd/{**cmd}", (string cmd, UiState ui, Telemetry tel, Hub hub) => RunCommand(cmd, ui, tel, hub));
app.MapGet("/api/commands", () => Results.Ok(Commands.List.Select(c => new
{
    command = c.Cmd,
    url = $"http://localhost:{settings.Port}/api/cmd/{c.Cmd}",
    what = c.What,
})));

var url = $"http://localhost:{settings.Port}/";

if (windowed && OperatingSystem.IsWindows())
{
    try { await app.StartAsync(); }
    catch (IOException e)
    {
        MfdWindow.Message($"WT-MFD could not start its server on port {settings.Port}.\n\n{e.Message}\n\n" +
                          "Another program may be using that port. Change \"Port\" in settings.json and try again.");
        return;
    }
    try { new MfdWindow(url + "?app=1", AppContext.BaseDirectory).Run(); }
    catch (Exception e) { MfdWindow.Message("The MFD window closed with an error:\n\n" + e.Message); }
    await app.StopAsync();
    Native.ApplyPendingUpdate(restart: false);
    return;
}

try { Console.Title = "WT-MFD"; } catch { /* no console */ }
Console.WriteLine($"""

  WT-MFD {asm.GetName().Version?.ToString(3)}
  ---------------------------------------------
  MFD page:        {url}
  Reading from:    {settings.WarThunderUrl} (War Thunder's own local data)
  Stream Deck:     {url}api/cmd/osb/t2 ... (full list: {url}api/commands)
  Network access:  {(settings.Lan ? "ON - tablets on your network can open the MFD" : "off (this PC only)")}
  Match logs:      {(settings.Log ? Path.Combine(AppContext.BaseDirectory, "logs") : "off")}

  Leave this window open while you play. Close it to stop the MFD.

""");

app.Run();
if (OperatingSystem.IsWindows()) Native.ApplyPendingUpdate(restart: false);
