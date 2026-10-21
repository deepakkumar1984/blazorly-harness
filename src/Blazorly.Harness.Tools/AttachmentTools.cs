using System.Text.Json;
using Blazorly.Harness.Core.Attachments;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record ReadImageArgs([property: System.Text.Json.Serialization.JsonPropertyName("file_path")] string FilePath);

public sealed record ReadImageOutput(string AttachmentId, string Path, string MimeType);

/// <summary>read_image: loads an image file into the attachments store so image-capable models see it on the next request.</summary>
public sealed class ReadImageTool(AttachmentService attachments) : ToolDefinition<ReadImageArgs, ReadImageOutput>
{
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly byte[] PngHeader = [0x89, (byte)'P', (byte)'N', (byte)'G'];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] GifHeader = [(byte)'G', (byte)'I', (byte)'F', (byte)'8'];
    private static readonly byte[] RiffHeader = [(byte)'R', (byte)'I', (byte)'F', (byte)'F'];
    private static readonly byte[] WebpTag = [(byte)'W', (byte)'E', (byte)'B', (byte)'P'];

    public override string Name => "read_image";

    public override string Description =>
        "Read an image file (PNG, JPEG, GIF, or WebP, up to 8MB) and attach it to the session so "
        + "image-capable models can see it on the next request. Independent files may be read in parallel.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["file_path"] = JsonSchema.String("Path to the image file, resolved against the session workspace."),
        },
        required: ["file_path"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["attachmentId"] = JsonSchema.String("Id of the stored attachment."),
            ["path"] = JsonSchema.String("Absolute path of the file that was read."),
            ["mimeType"] = JsonSchema.String("MIME type sniffed from the file's magic bytes."),
        },
        required: ["attachmentId", "path", "mimeType"]);

    public override int? TimeoutMs => 10_000;

    protected override bool IsConcurrencySafeTyped(ReadImageArgs args) => true;

    protected override async Task<ReadImageOutput> ExecuteTyped(ReadImageArgs args, ToolRunContext exec)
    {
        var root = exec.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
        var path = Path.GetFullPath(args.FilePath, root);
        if (!File.Exists(path)) throw new ToolException("FILE_NOT_FOUND", $"file '{path}' does not exist");
        var data = await File.ReadAllBytesAsync(path, exec.Signal).ConfigureAwait(false);
        if (data.Length > MaxBytes)
            throw new ToolException("IMAGE_TOO_LARGE", $"image is {data.Length} bytes; the cap is {MaxBytes} bytes");
        var mimeType = SniffMimeType(data)
            ?? throw new ToolException("UNSUPPORTED_IMAGE", $"'{path}' is not a supported image (expected PNG, JPEG, GIF, or WebP)");
        var attachmentId = await attachments.SaveAsync(exec.Session.Id, data, mimeType, exec.Signal).ConfigureAwait(false);
        return new ReadImageOutput(attachmentId, path, mimeType);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(ReadImageArgs args, ReadImageOutput output)
        => [new TextBlock($"Image read and attached as {output.AttachmentId} ({output.MimeType}). It will be visible to image-capable models on the next request.")];

    protected override ToolCallView? PresentCallTyped(ReadImageArgs args) => new()
    {
        Card = "generic",
        Kind = "read",
        Title = Path.GetFileName(args.FilePath),
        Path = args.FilePath,
    };

    internal static string? SniffMimeType(byte[] data)
    {
        if (HasPrefix(data, 0, PngHeader)) return "image/png";
        if (HasPrefix(data, 0, JpegHeader)) return "image/jpeg";
        if (HasPrefix(data, 0, GifHeader)) return "image/gif";
        if (HasPrefix(data, 0, RiffHeader) && HasPrefix(data, 8, WebpTag)) return "image/webp";
        return null;
    }

    private static bool HasPrefix(byte[] data, int offset, byte[] prefix)
    {
        if (data.Length < offset + prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[offset + i] != prefix[i]) return false;
        }
        return true;
    }
}

/// <summary>Mounts the attachment family: the attachments store plus read_image.</summary>
public sealed class AttachmentPlugin(string? rootDir = null) : HarnessPlugin
{
    public override string Name => "attachments";
    public override string[] Inject { get; } = ["tools"];

    public string? RootDir { get; } = rootDir;
    public AttachmentService? Service { get; private set; }

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        Service = ctx.TryGet<AttachmentService>(AttachmentService.ServiceKey) ?? AttachmentService.Mount(ctx, RootDir);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
        ctx.Effect(tools.Register(new ReadImageTool(Service)).Dispose);
        return Task.CompletedTask;
    }
}
