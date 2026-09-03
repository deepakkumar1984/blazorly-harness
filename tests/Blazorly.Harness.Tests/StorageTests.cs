using System.Text.Json;
using Blazorly.Harness.Core.Attachments;
using Blazorly.Harness.Core.Credentials;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

public class SqlitePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-sqlite-" + Guid.NewGuid().ToString("N")[..8]);

    private SqliteSessionPersistence NewPersistence() => new(Path.Combine(_root, "sessions.db"));

    [Fact]
    public async Task RoundTripsEventsWithSurfaceOpsAndPluginTypes()
    {
        using var persistence = NewPersistence();
        var session = new Session(new SessionHeader
        {
            Id = "sq-round",
            CreatedAt = 5_000,
            Cwd = "/tmp/proj",
            ParentSession = "sq-parent",
            DelegationDepth = 1,
            AgentPreset = "test",
        });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("first"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("second"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("summary of both"),
            new Session.AppendOptions(SourceEventSeqs: [1, 2], SurfaceOp: new SurfaceOp.Replace(0, 1)));
        session.Append("plugin/custom", new { hello = "world", n = 3 }, new Session.AppendOptions(Ignorable: true));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));

        await persistence.CreateAsync(session.Header);
        await persistence.AppendAsync(session.Id, session.Events);

        var (header, events) = await persistence.LoadAsync(session.Id);
        Assert.Equal(session.Header, header); // record equality covers every header field
        Assert.Equal(session.Events.Count, events.Count);
        for (var i = 0; i < session.Events.Count; i++)
        {
            var expected = session.Events[i];
            var actual = events[i];
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Seq, actual.Seq);
            Assert.Equal(expected.Time, actual.Time);
            Assert.Equal(expected.Data.GetRawText(), actual.Data.GetRawText());
            Assert.Equal(expected.SourceEventSeqs ?? [], actual.SourceEventSeqs ?? []);
            Assert.Equal(expected.Ignorable, actual.Ignorable);
            Assert.Equal(expected.SurfaceOp, actual.SurfaceOp);
        }

        var replaced = events[3];
        var replace = Assert.IsType<SurfaceOp.Replace>(replaced.SurfaceOp);
        Assert.Equal(0, replace.Start);
        Assert.Equal(1, replace.End);
        Assert.Equal([1, 2], replaced.SourceEventSeqs ?? []);
        Assert.Null(replaced.Ignorable);
        Assert.Null(events[0].SurfaceOp);
        Assert.True(events[4].Ignorable);
        Assert.Equal("""{"hello":"world","n":3}""", events[4].Data.GetRawText());
    }

    [Fact]
    public async Task AppendRejectsWrongNextSeq()
    {
        using var persistence = NewPersistence();
        var header = new SessionHeader { Id = "sq-seq", CreatedAt = 5_000, Cwd = "/tmp/proj" };
        await persistence.CreateAsync(header);

        var session = new Session(header);
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        await persistence.AppendAsync(header.Id, session.Events);

        var stale = new SessionEvent
        {
            Type = SessionEventTypes.TurnEnd,
            Seq = 0, // the store already holds seq 0
            Time = 6_000,
            Data = SessionJson.ToElement(new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed())),
        };
        var ex = await Assert.ThrowsAsync<HarnessException>(() => persistence.AppendAsync(header.Id, [stale]));
        Assert.Equal("SESSION_SEQ_MISMATCH", ex.Code);

        var (_, events) = await persistence.LoadAsync(header.Id);
        Assert.Single(events); // the rejected batch left no partial rows
    }

    [Fact]
    public async Task ListsNewestFirst()
    {
        using var persistence = NewPersistence();
        foreach (var (id, createdAt) in new[] { ("sq-a", 5_000L), ("sq-c", 9_000L), ("sq-b", 7_000L) })
        {
            await persistence.CreateAsync(new SessionHeader { Id = id, CreatedAt = createdAt, Cwd = "/tmp/proj" });
        }
        var listed = await persistence.ListAsync();
        Assert.Equal(["sq-c", "sq-b", "sq-a"], listed.Select(h => h.Id).ToArray());
    }

    [Fact]
    public async Task SessionsAreIsolated()
    {
        using var persistence = NewPersistence();
        var one = new Session(new SessionHeader { Id = "sq-one", CreatedAt = 5_000, Cwd = "/tmp/one" });
        var two = new Session(new SessionHeader { Id = "sq-two", CreatedAt = 6_000, Cwd = "/tmp/two" });
        foreach (var s in new[] { one, two })
        {
            await persistence.CreateAsync(s.Header);
            s.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
            s.Append(SessionEventTypes.UserMessage, Message.CreateUserText($"hello {s.Id}"),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
            s.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
            await persistence.AppendAsync(s.Id, s.Events);
        }

        var (headerOne, eventsOne) = await persistence.LoadAsync("sq-one");
        var (headerTwo, eventsTwo) = await persistence.LoadAsync("sq-two");
        Assert.Equal("/tmp/one", headerOne.Cwd);
        Assert.Equal("/tmp/two", headerTwo.Cwd);
        Assert.Equal(3, eventsOne.Count);
        Assert.Equal(3, eventsTwo.Count);
        Assert.Equal("hello sq-one", SessionEventRead.MessageOf(eventsOne[1]).FlattenText());
        Assert.Equal("hello sq-two", SessionEventRead.MessageOf(eventsTwo[1]).FlattenText());
    }

    [Fact]
    public async Task CreateRejectsDuplicateIdsAndLoadRejectsMissing()
    {
        using var persistence = NewPersistence();
        var header = new SessionHeader { Id = "sq-dup", CreatedAt = 5_000, Cwd = "/tmp/proj" };
        await persistence.CreateAsync(header);
        var duplicate = await Assert.ThrowsAsync<HarnessException>(() => persistence.CreateAsync(header));
        Assert.Equal("SESSION_ALREADY_EXISTS", duplicate.Code);

        var missing = await Assert.ThrowsAsync<HarnessException>(() => persistence.LoadAsync("sq-missing"));
        Assert.Equal("SESSION_NOT_FOUND", missing.Code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class CredentialsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-creds-" + Guid.NewGuid().ToString("N")[..8]);

    private string NewFilePath() => Path.Combine(_root, "credentials.json");

    [Fact]
    public async Task EnvVarWinsOverFile()
    {
        var name = "BLZLY_TEST_CRED_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(name, null);
        try
        {
            var service = new CredentialsService(NewFilePath());
            await service.SetAsync(name, "file-secret");
            Assert.Equal("file-secret", service.Resolve(name)!.Value);
            Assert.Equal(CredentialsService.SourceFile, service.Resolve(name)!.Source);

            Environment.SetEnvironmentVariable(name, "env-secret");
            var resolved = service.Resolve(name)!;
            Assert.Equal("env-secret", resolved.Value);
            Assert.Equal(CredentialsService.SourceEnv, resolved.Source);

            Environment.SetEnvironmentVariable(name, ""); // empty env falls through to the file
            Assert.Equal("file-secret", service.Resolve(name)!.Value);
            Assert.Equal(CredentialsService.SourceFile, service.Resolve(name)!.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task FileSetGetUnsetRoundTrips()
    {
        var path = NewFilePath();
        var service = new CredentialsService(path);
        var first = "blzly_first_" + Guid.NewGuid().ToString("N")[..8];
        var second = "blzly_second_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Null(service.Resolve(first));

        await service.SetAsync(first, "one");
        await service.SetAsync(second, "two");
        Assert.Equal("one", service.Resolve(first)!.Value);

        var reopened = new CredentialsService(path); // a fresh service sees the same store
        Assert.Equal("one", reopened.Resolve(first)!.Value);
        Assert.Equal(CredentialsService.SourceFile, reopened.Resolve(first)!.Source);

        await reopened.UnsetAsync(first);
        Assert.Null(reopened.Resolve(first));
        Assert.Equal("two", reopened.Resolve(second)!.Value);
    }

    [Fact]
    public async Task DescribeNeverLeaksValues()
    {
        var service = new CredentialsService(NewFilePath());
        var first = "blzly_zz_" + Guid.NewGuid().ToString("N")[..8];
        var second = "blzly_aa_" + Guid.NewGuid().ToString("N")[..8];
        await service.SetAsync(first, "super-secret-value-1");
        await service.SetAsync(second, "super-secret-value-2");

        var described = service.Describe();
        Assert.Equal([second, first], described.Select(d => d.Name).ToArray());
        Assert.All(described, d => Assert.Equal(CredentialsService.SourceFile, d.Source));
        Assert.DoesNotContain("super-secret-value-1", JsonSerializer.Serialize(described));
        Assert.DoesNotContain("super-secret-value-2", JsonSerializer.Serialize(described));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class AttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-att-" + Guid.NewGuid().ToString("N")[..8]);

    private AttachmentService NewService() => new(Path.Combine(_root, "attachments"));

    private static readonly byte[] TinyPng = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

    [Fact]
    public async Task SaveThenReadRoundTripsBytes()
    {
        var service = NewService();
        var id = await service.SaveAsync("session-a", TinyPng, "image/png");

        Assert.StartsWith("att_", id);
        var read = await service.ReadAsync(id);
        Assert.NotNull(read);
        Assert.Equal(TinyPng, read!.Data);
        Assert.Equal("image/png", read.MimeType);
        Assert.Equal("session-a", read.SessionId);
        Assert.Null(await service.ReadAsync("att_does_not_exist"));
    }

    [Fact]
    public async Task ListsAttachmentsPerSession()
    {
        var service = NewService();
        var a1 = await service.SaveAsync("session-a", [1, 2], "image/png");
        var a2 = await service.SaveAsync("session-a", [3, 4], "image/png");
        var b1 = await service.SaveAsync("session-b", [5, 6], "image/jpeg");

        var inA = service.List("session-a");
        Assert.Equal([a1, a2], inA.Select(m => m.Id).ToArray());
        Assert.All(inA, m => Assert.Equal("session-a", m.SessionId));
        Assert.Equal([b1], service.List("session-b").Select(m => m.Id).ToArray());
        Assert.Empty(service.List("session-c"));
    }

    [Fact]
    public async Task ReadImageToolAttachesValidImages()
    {
        var cwd = Path.Combine(_root, "project");
        Directory.CreateDirectory(cwd);
        await using var harness = TestHarness.Create(cwd: cwd);
        new AttachmentPlugin(Path.Combine(_root, "attachments")).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd);

        var imagePath = Path.Combine(cwd, "tiny.png");
        await File.WriteAllBytesAsync(imagePath, TinyPng);

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "read_image",
            Arguments = JsonSerializer.SerializeToElement(new { file_path = "tiny.png" }),
            CallId = "c1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.False(result.IsError, result.Error?.Message ?? "no error");

        var attachmentId = result.Value!.Value.GetProperty("attachmentId").GetString()!;
        Assert.StartsWith("att_", attachmentId);
        Assert.Equal("image/png", result.Value!.Value.GetProperty("mimeType").GetString());
        Assert.Equal(imagePath, result.Value!.Value.GetProperty("path").GetString());

        var service = harness.Ctx.Get<AttachmentService>("attachments");
        var stored = await service.ReadAsync(attachmentId);
        Assert.NotNull(stored);
        Assert.Equal(TinyPng, stored!.Data);
    }

    [Fact]
    public async Task ReadImageToolRejectsNonImages()
    {
        var cwd = Path.Combine(_root, "project");
        Directory.CreateDirectory(cwd);
        await using var harness = TestHarness.Create(cwd: cwd);
        new AttachmentPlugin(Path.Combine(_root, "attachments")).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd);

        var textPath = Path.Combine(cwd, "notes.txt");
        await File.WriteAllTextAsync(textPath, "not an image");

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "read_image",
            Arguments = JsonSerializer.SerializeToElement(new { file_path = "notes.txt" }),
            CallId = "c1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.True(result.IsError);
        Assert.Equal("UNSUPPORTED_IMAGE", result.Error!.Info!.Code);
    }

    [Fact]
    public async Task ReadImageToolEnforcesTheSizeCap()
    {
        var cwd = Path.Combine(_root, "project");
        Directory.CreateDirectory(cwd);
        await using var harness = TestHarness.Create(cwd: cwd);
        new AttachmentPlugin(Path.Combine(_root, "attachments")).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd);

        var oversized = new byte[ReadImageTool.MaxBytes + 1];
        OversizedPngHeader().CopyTo(oversized);
        var imagePath = Path.Combine(cwd, "huge.png");
        await File.WriteAllBytesAsync(imagePath, oversized);

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "read_image",
            Arguments = JsonSerializer.SerializeToElement(new { file_path = "huge.png" }),
            CallId = "c1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.True(result.IsError);
        Assert.Equal("IMAGE_TOO_LARGE", result.Error!.Info!.Code);

        static byte[] OversizedPngHeader() => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
