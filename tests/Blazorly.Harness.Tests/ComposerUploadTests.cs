using System.Text;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Web.Services;
using Session = Blazorly.Harness.Core.Sessions.Session;

namespace Blazorly.Harness.Tests;

/// <summary>Composer attachment pipeline: upload classification (image / text / binary
/// by FILENAME, because browsers paste text files with empty or octet-stream MIME) and
/// the user message blocks each kind produces at send time.</summary>
[Collection("BlazorlyHome")] // shares the serialized BLAZORLY_HOME-mutating test group
public sealed class ComposerUploadTests
{
    private sealed record Scope(SessionFacade Facade, HarnessBootstrapper Boot, string Home);

    private static async Task<Scope> CreateFacadeAsync()
    {
        var home = Path.Combine(Path.GetTempPath(), "blazorly-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var previous = Environment.GetEnvironmentVariable("BLAZORLY_HOME");
        Environment.SetEnvironmentVariable("BLAZORLY_HOME", home);
        try
        {
            var boot = new HarnessBootstrapper();
            await boot.StartAsync(default);
            boot.Workspaces.Add("Uploads", home);
            // No network in tests: swap the default route for the scripted adapter.
            boot.Llm.RegisterAdapter(new ScriptedLlmAdapter(_ => Scripted.Text("ok")));
            boot.Loop.DefaultSelection = new LlmCallConfig { Provider = "scripted", Model = "test" };
            return new Scope(new SessionFacade(boot, new UiEventBroker()), boot, home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLAZORLY_HOME", previous);
        }
    }

    private static async Task CleanupAsync(HarnessBootstrapper boot, string home)
    {
        await boot.DisposeAsync();
        try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
    }

    private static async Task WaitForUserMessageAsync(Session session, string contains)
    {
        // Followup runs the turn in the background; the user message hits the durable
        // log once the turn starts, not synchronously with PromptAsync. System-injected
        // maintenance messages also land in the log, so wait for OUR text specifically.
        for (var i = 0; i < 100; i++)
        {
            if (session.Events.Any(e => e.Type == SessionEventTypes.UserMessage
                    && SessionEventRead.MessageOf(e).FlattenText().Contains(contains)))
                return;
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Upload_ClassifiesByFilename_NotJustMime()
    {
        var scope = await CreateFacadeAsync();
        try
        {
            var (facade, boot, home) = scope;
            var session = facade.CreateSession();

            var png = await facade.UploadFileAsync(session.Id, [1, 2, 3], "image/png", "shot.png");            Assert.Equal("image", png.Kind);

            // Browsers paste text files with octet-stream or empty MIME; the filename decides.
            var txt = await facade.UploadFileAsync(session.Id, Encoding.UTF8.GetBytes("hello notes"), "application/octet-stream", "notes.txt");
            Assert.Equal("text", txt.Kind);
            Assert.Equal("hello notes", txt.TextContent);

            var pdf = await facade.UploadFileAsync(session.Id, [0x25, 0x50, 0x46, 0x46], "application/pdf", "report.pdf");
            Assert.Equal("document", pdf.Kind);
            Assert.Null(pdf.TextContent);
        }
        finally
        {
            await CleanupAsync(scope.Boot, scope.Home);
        }
    }

    [Fact]
    public async Task Prompt_ImageAttachment_CarriesImageBlock()
    {
        var scope = await CreateFacadeAsync();
        try
        {
            var (facade, boot, home) = scope;
            var session = facade.CreateSession();
            var up = await facade.UploadFileAsync(session.Id, [1, 2, 3, 4], "image/png", "shot.png");
            await facade.PromptAsync(session.Id, "what is in this screenshot?", "queue", [up.Id]);
            await WaitForUserMessageAsync(session, "screenshot");

            var logged = session.Events.FirstOrDefault(e => e.Type == SessionEventTypes.UserMessage);
            Assert.NotNull(logged);
            var message = SessionEventRead.MessageOf(logged!);
            Assert.Contains(message.Content.OfType<ImageBlock>(), b => b.AttachmentId == up.Id);
            Assert.Contains("screenshot", message.FlattenText());
        }
        finally
        {
            await CleanupAsync(scope.Boot, scope.Home);
        }
    }

    [Fact]
    public async Task Prompt_TextAttachment_InlinesContent_BinaryDropsWorkspaceCopy()
    {
        var scope = await CreateFacadeAsync();
        try
        {
            var (facade, boot, home) = scope;
            var session = facade.CreateSession();
            var txt = await facade.UploadFileAsync(session.Id, Encoding.UTF8.GetBytes("the notes body"), "text/plain", "notes.txt");
            await facade.PromptAsync(session.Id, "read my notes", "queue", [txt.Id]);
            await WaitForUserMessageAsync(session, "read my notes");

            var workspaceRoot = session.Header.Cwd!;
            var logged = session.Events.FirstOrDefault(e => e.Type == SessionEventTypes.UserMessage);
            Assert.NotNull(logged);
            var message = SessionEventRead.MessageOf(logged!);
            Assert.Contains("the notes body", message.FlattenText());
            Assert.Contains("notes.txt", message.FlattenText());

            // Binary path: the file lands under the session workspace with its extension,
            // and the message points the agent at the copy.
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7 fake");
            var pdf = await facade.UploadFileAsync(session.Id, pdfBytes, "application/pdf", "report.pdf");
            await facade.PromptAsync(session.Id, "summarize the report", "queue", [pdf.Id]);
            await WaitForUserMessageAsync(session, "summarize the report");
            var dropped = Path.Combine(workspaceRoot, ".blazorly-uploads", "report.pdf");
            Assert.True(File.Exists(dropped), $"expected workspace copy at {dropped}");
            Assert.Equal(pdfBytes, await File.ReadAllBytesAsync(dropped));
            var logged2 = SessionEventRead.MessageOf(session.Events
                .Where(e => e.Type == SessionEventTypes.UserMessage)
                .Last(e => SessionEventRead.MessageOf(e).FlattenText().Contains("summarize the report")));
            Assert.Contains(".blazorly-uploads", logged2.FlattenText());
        }
        finally
        {
            await CleanupAsync(scope.Boot, scope.Home);
        }
    }
}
