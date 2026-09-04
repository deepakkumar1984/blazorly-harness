using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Components;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Web;

/// <summary>The UI host, callable from the product launcher (`blazorly serve`) and the
/// standalone web project alike. Binds http://localhost:5080 unless ASPNETCORE_URLS
/// or --port say otherwise; --no-open suppresses the welcome browser tab.</summary>
public static class UiHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var uiArgs = UiArgs.Parse(args);
        if (uiArgs.WantsVersion)
        {
            Console.WriteLine(UiVersion.Text);
            return 0;
        }

        var builder = WebApplication.CreateBuilder(args);

        // Published binaries have no launchSettings.json: bind :5080 explicitly unless
        // the environment (ASPNETCORE_URLS / --urls) already chose something.
        if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is not { Length: > 0 }
            && args.All(a => !a.StartsWith("--urls", StringComparison.Ordinal)))
        {
            builder.WebHost.UseUrls($"http://localhost:{uiArgs.Port}");
        }

        // The packaged product talks to humans on stdout, not through ASP.NET's info
        // chatter (DataProtection keys, hosting lifetime). Dev runs keep full logs.
        // Hosting errors are handled locally (see the bind-failure catch below).
        if (Assembly.GetEntryAssembly()?.GetName().Name != "Blazorly.Harness.Web")
        {
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Critical);
        }

        builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddHttpClient();

    builder.Services.AddSingleton<HarnessBootstrapper>();
    builder.Services.AddSingleton<UiEventBroker>();
    builder.Services.AddSingleton<UiInteractions>();
    builder.Services.AddSingleton<SessionFacade>();
    builder.Services.AddSingleton(sp =>
    {
        var harness = sp.GetRequiredService<HarnessBootstrapper>();
        return new ConversationAssembler(harness.Tools, harness.Meter);
    });
    builder.Services.AddSingleton<MarkdownService>();

    var app = builder.Build();

    // Boot the harness composition before serving anything.
    var bootstrapper = app.Services.GetRequiredService<HarnessBootstrapper>();
    await bootstrapper.StartAsync(default);
    var broker = app.Services.GetRequiredService<UiEventBroker>();
    var interactions = app.Services.GetRequiredService<UiInteractions>();
    interactions.Mount(bootstrapper);

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
    }
    app.UseStatusCodePagesWithReExecute("/not-found");app.UseWebSockets();
    app.UseAntiforgery();

        // Static assets: when the Web project is the host (dev / its own publish) the
        // optimized manifest pipeline applies. The packaged launcher (entry = blazorly)
        // can't remap the manifest's build-time paths — it serves wwwroot plainly from
        // beside the binary (ContentRoot tracks the CWD, the archive layout does not).
        if (Assembly.GetEntryAssembly()?.GetName().Name == "Blazorly.Harness.Web")
            app.MapStaticAssets();
        else
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot")),
            });

    // ---- REST surface (mirrors dsh's apiproxy session domain) ----

    app.MapGet("/api/session.list", async (SessionFacade facade) =>
        JsonSerializer.Serialize(new
        {
            sessions = (await facade.ListPersistedAsync()).Select(h => new { h.Id, h.CreatedAt, h.Cwd, h.ParentSession }).ToList(),
        }));

    app.MapPost("/api/session.create", (SessionFacade facade) =>
    {
        var session = facade.CreateSession();
        return Results.Json(new { session.Id });
    });

    app.MapPost("/api/session.prompt", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<PromptRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.SessionId) || string.IsNullOrWhiteSpace(body.Content))
            return Results.BadRequest(new { error = "sessionId and content are required" });
        await facade.PromptAsync(body.SessionId, body.Content, string.IsNullOrWhiteSpace(body.Mode) ? "queue" : body.Mode);
        await facade.FlushAsync(body.SessionId);
        return Results.Json(new { ok = true });
    });

    app.MapPost("/api/session.cancel", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<PromptRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.SessionId)) return Results.BadRequest();
        facade.Cancel(body.SessionId);
        await facade.FlushAsync(body.SessionId);
        return Results.Json(new { ok = true });
    });

    app.MapGet("/api/session.history", async (string id, SessionFacade facade) =>
    {
        var session = await facade.OpenSessionAsync(id);
        return Results.Json(new
        {
            id = session.Id,
            seq = session.Seq,
            events = session.Events.Select(e => new
            {
                e.Type,
                e.Seq,
                e.Time,
                data = e.Data,
            }),
        });
    });

    app.MapGet("/api/session.projection", async (string sessionId, string name, HarnessBootstrapper harness) =>
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "sessionId and name are required" });
        try
        {
            var (value, throughEvents) = await harness.Projections.ProjectAsync(sessionId, name);
            return Results.Json(new { sessionId, name, throughEvents, value });
        }
        catch (HarnessException ex) when (ex.Code is "SESSION_NOT_FOUND" or "UNKNOWN_PROJECTION")
        {
            return Results.NotFound(new { error = ex.Message });
        }
    });

    app.MapGet("/api/session.export", async (string id, SessionFacade facade) =>
    {
        if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "id is required" });
        try
        {
            var session = await facade.OpenSessionAsync(id);
            var zip = SessionExport.BuildZip(session.Header, session.Events);
            return Results.File(zip, "application/zip", $"{session.Id}.zip");
        }
        catch (HarnessException ex) when (ex.Code is "SESSION_NOT_FOUND" or "NO_PERSISTENCE")
        {
            return Results.NotFound(new { error = ex.Message });
        }
    });

    app.MapPost("/api/session.fork", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<ForkRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.SessionId)) return Results.BadRequest();
        var child = facade.Fork(body.SessionId, body.AtSeq);
        await facade.FlushAsync(child.Id);
        return Results.Json(new { id = child.Id });
    });

    app.MapPost("/api/interaction.answer", async (HttpContext http, UiInteractions ui) =>
    {
        var body = await http.Request.ReadFromJsonAsync<AnswerRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Id)) return Results.BadRequest();
        var ok = ui.TryAnswer(body.Id, body.Answer ?? "");
        return Results.Json(new { ok });
    });

    // ---- workspace + host surface ----

    app.MapGet("/api/workspace.list", (SessionFacade facade) => Results.Json(new
    {
        workspaces = facade.Workspaces().Select(w => new { w.Id, w.Name, w.Root, w.Order }),
    }));

    app.MapPost("/api/workspace.add", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<WorkspaceRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Root)) return Results.BadRequest(new { error = "root is required" });
        try
        {
            var workspace = facade.AddWorkspace(body.Name ?? "", body.Root);
            return Results.Json(new { workspace.Id, workspace.Name, workspace.Root });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapPost("/api/workspace.remove", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<WorkspaceRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Id)) return Results.BadRequest();
        facade.RemoveWorkspace(body.Id);
        return Results.Json(new { ok = true });
    });

    app.MapGet("/api/host.browse", (string? path) =>
    {
        try
        {
            var entries = DirectoryBrowser.List(path ?? "/");
            return Results.Json(new
            {
                path = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "/" : path),
                parent = Directory.GetParent(Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "/" : path))?.FullName,
                entries,
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapPost("/api/session.rename", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<PromptRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.SessionId)) return Results.BadRequest();
        facade.RenameSession(body.SessionId, body.Content ?? "");
        await facade.FlushAsync(body.SessionId);
        return Results.Json(new { ok = true });
    });

    app.MapPost("/api/session.archive", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<ArchiveRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.SessionId)) return Results.BadRequest();
        facade.Archive(body.SessionId, body.Archived);
        return Results.Json(new { ok = true });
    });

    app.MapPost("/api/session.command", async (HttpContext http, SessionFacade facade) =>
    {
        var body = await http.Request.ReadFromJsonAsync<PromptRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.SessionId) || string.IsNullOrWhiteSpace(body.Content))
            return Results.BadRequest(new { error = "sessionId and content are required" });
        var outcome = facade.TryCommand(body.SessionId, body.Content);
        if (outcome is null) return Results.BadRequest(new { error = "not a command" });
        await facade.FlushAsync(body.SessionId);
        return Results.Json(new { outcome.Name, outcome.Ok, outcome.Text });
    });

    app.MapGet("/api/session.search", (string q, SessionFacade facade) =>
        Results.Json(new { hits = facade.Search(q).Select(h => new { h.SessionId, h.Title, h.Kind, h.Snippet }) }));

    app.MapGet("/api/session.files", (string sessionId, string? q, SessionFacade facade) =>
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Results.BadRequest(new { error = "sessionId is required" });
        var files = facade.FileCandidates(sessionId, q);
        return Results.Json(new { files = files.Select(f => new { f.Path, isDir = f.IsDir, f.Size }) });
    });

    // ---- credentials + jobs surface ----

    app.MapGet("/api/telemetry", (HarnessBootstrapper harness) => Results.Json(
        harness.Telemetry is { } telemetry ? telemetry.Snapshot() : new { generatedAt = 0L, enabled = false, days = Array.Empty<object>() }));

    app.MapGet("/api/llm.providers", (HarnessBootstrapper harness) => Results.Json(new
    {
        providers = harness.Llm.ListProviders().Select(p => new { id = p, models = harness.Llm.ListModels(p) }),
        catalog = ProviderCatalog.Providers,
    }));

    app.MapPost("/api/llm.discover", async (HttpContext http, HarnessBootstrapper harness) =>
    {
        var body = await http.Request.ReadFromJsonAsync<DiscoverRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Provider)) return Results.BadRequest(new { error = "provider is required" });

        // Same path the Settings UI uses: fetch GET /models, persist the list, rebuild the route.
        var knownRoute = body.Provider == harness.Settings.Provider
            || harness.Settings.CustomProviders.Any(c => c.Name == body.Provider);
        if (!knownRoute) return Results.BadRequest(new { error = "unknown provider route" });

        var (ids, error) = await harness.DiscoverModelsAsync(body.Provider, body.BaseUrl, body.ApiKey);
        if (error is not null) return Results.BadRequest(new { error });
        return Results.Json(new { provider = body.Provider, models = harness.RuntimeModels(body.Provider) });
    });

    app.MapGet("/api/credentials.describe", (HarnessBootstrapper harness) => Results.Json(new
    {
        names = harness.Credentials.Describe().Select(c => new { c.Name, c.Source }),
    }));

    app.MapPost("/api/credentials.set", async (HttpContext http, HarnessBootstrapper harness) =>
    {
        var body = await http.Request.ReadFromJsonAsync<CredentialRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest();
        await harness.Credentials.SetAsync(body.Name, body.Value ?? "");
        return Results.Json(new { ok = true });
    });

    app.MapPost("/api/credentials.unset", async (HttpContext http, HarnessBootstrapper harness) =>
    {
        var body = await http.Request.ReadFromJsonAsync<CredentialRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest();
        await harness.Credentials.UnsetAsync(body.Name);
        return Results.Json(new { ok = true });
    });

    app.MapGet("/api/jobs.list", (HarnessBootstrapper harness) => Results.Json(new
    {
        jobs = harness.Jobs.List().Select(j => new { j.Id, j.Kind, j.Description, j.Status, j.ExitCode, StartedAt = j.StartedAt.ToUnixTimeMilliseconds() }),
    }));

    app.MapGet("/api/events", async (HttpContext http, UiEventBroker brokerRef, SessionFacade facade) =>
    {
        if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { error = "websocket required" });
        var sessionId = http.Request.Query["sessionId"].ToString();
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        if (!string.IsNullOrEmpty(sessionId))
        {
            // Opening a session over the API keeps it live for event replay.
            try { await facade.OpenSessionAsync(sessionId); } catch { /* unknown session streams anyway */ }
        }
        var queue = Channel.CreateUnbounded<UiEventBroker.Frame>();
        using var subscription = brokerRef.Subscribe(frame =>
        {
            if (string.IsNullOrEmpty(sessionId) || frame.SessionId == sessionId) queue.Writer.TryWrite(frame);
            return Task.CompletedTask;
        });
        try
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                var session = facade.Harness.Sessions.Get(sessionId);
                if (session is not null)
                {
                    foreach (var e in session.Events)
                    {
                        await SendEvent(socket, sessionId, e);
                    }
                }
            }
            while (socket.State == WebSocketState.Open)
            {
                var frame = await queue.Reader.ReadAsync(http.RequestAborted);
                await SendEvent(socket, frame.SessionId, frame.Event);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        catch (WebSocketException)
        {
            // client disconnected
        }
        return Results.Empty;
    });

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

        var url = $"http://localhost:{uiArgs.Port}";
        Console.WriteLine($"blazorly {UiVersion.Text} — UI at {url} (Ctrl+C to stop)");
        if (!uiArgs.NoOpen)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                OpenBrowser(url);
            });
        }

        try
        {
            app.Run();
        }
        catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
        {
            // a busy port is a user-facing condition, not a crash: say what to do, exit cleanly
            Console.Error.WriteLine(
                $"blazorly: port {uiArgs.Port} is already in use — another blazorly, or another app?\n" +
                $"  stop it (macOS/Linux: lsof -ti :{uiArgs.Port} | xargs kill; Windows: netstat -ano | findstr :{uiArgs.Port})\n" +
                $"  or start elsewhere: blazorly --port {uiArgs.Port + 1}");
            return 1;
        }
        return 0;
    }

    private static async Task SendEvent(WebSocket socket, string sessionId, SessionEvent e)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "session/event",
            sessionId,
            @event = new { e.Type, e.Seq, e.Time, data = e.Data },
        });
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)),
            WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static void OpenBrowser(string url)
    {
        // best-effort welcome tab; failures (headless hosts, missing xdg-open) are silent
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start {url}") { CreateNoWindow = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch
        {
            // no opener on PATH — the URL is printed anyway
        }
    }
}

public sealed record PromptRequest(string? SessionId, string? Content, string? Mode);
public sealed record ForkRequest(string? SessionId, int? AtSeq);
public sealed record AnswerRequest(string? Id, string? Answer);
public sealed record WorkspaceRequest(string? Id, string? Name, string? Root);
public sealed record ArchiveRequest(string? SessionId, bool Archived);
public sealed record CredentialRequest(string? Name, string? Value);
public sealed record DiscoverRequest(string? Provider, string? BaseUrl = null, string? ApiKey = null);
