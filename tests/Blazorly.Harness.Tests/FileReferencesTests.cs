using Blazorly.Harness.Core.Attachments;
using Blazorly.Harness.Core.Context;
using Blazorly.Harness.Llm;
using Xunit;

namespace Blazorly.Harness.Tests;

public class FileReferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-filerefs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _attRoot;

    public FileReferencesTests()
    {
        Directory.CreateDirectory(_root);
        _attRoot = Path.Combine(_root, "attachments");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "line one\nline two\n");
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', FileReferences.MaxTextBytes + 10_000));
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [1, 2, 0, 3, 4]); // NUL → binary
        File.WriteAllBytes(Path.Combine(_root, "pic.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "Console.WriteLine();\n");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "junk"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "junk", "deep.txt"), "junk\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Task<FileReferenceResult> Expand(string text) =>
        FileReferences.ExpandAsync(text, _root, "session-test", new AttachmentService(_attRoot));

    [Fact]
    public void Parse_FindsTokens_AtWordBoundaries()
    {
        var tokens = FileReferences.Parse("see @src/Program.cs and @notes.txt please");
        Assert.Equal(["src/Program.cs", "notes.txt"], tokens.Select(t => t.Token).ToArray());

        Assert.Empty(FileReferences.Parse("mail me at a@b.com"));       // mid-word @ never matches
        Assert.Empty(FileReferences.Parse("no references here"));
        Assert.Equal(["here", "there"], FileReferences.Parse("@here and @there").Select(t => t.Token).ToArray());
    }

    [Fact]
    public async Task Expand_AttachesTextFile_WithHeaderAndBody()
    {
        var result = await Expand("read @notes.txt for context");
        Assert.Equal(2, result.Blocks.Count); // original text + attachment
        var attached = Assert.IsType<TextBlock>(result.Blocks[1]);
        Assert.Contains("@notes.txt", attached.Text);
        Assert.Contains("--- end @notes.txt ---", attached.Text);
        Assert.Contains("line one", attached.Text);
        var file = Assert.Single(result.Attached, a => a.Kind == "text");
        Assert.Equal(Path.Combine(_root, "notes.txt"), file.Path);
    }

    [Fact]
    public async Task Expand_DirectoryAndMissing_NoticeOrFailSoft()
    {
        var dir = await Expand("look at @src");
        Assert.Contains(dir.Attached, a => a.Kind == "directory");
        Assert.Equal(2, dir.Blocks.Count); // notice block appended

        var pathy = await Expand("read @src/Missing.cs");
        Assert.Contains(pathy.Attached, a => a.Kind == "missing");
        Assert.Equal(2, pathy.Blocks.Count); // path-shaped miss gets a notice

        var bare = await Expand("ping @here");
        // A bare word that resolves to nothing is prose: no attachment, no notice block.
        Assert.Contains(bare.Attached, a => a.Kind == "missing");
        Assert.Single(bare.Blocks);
    }

    [Fact]
    public async Task Expand_BinaryNotAttached_ImageStored()
    {
        var binary = await Expand("check @blob.bin");
        Assert.Contains(binary.Attached, a => a.Kind == "binary" && a.Token == "blob.bin");
        Assert.Contains(binary.Blocks.OfType<TextBlock>(), b => b.Text.Contains("not attached"));

        var image = await Expand("view @pic.png");
        Assert.Contains(image.Attached, a => a.Kind == "image" && a.Path.EndsWith("pic.png"));
        var block = Assert.Single(image.Blocks.OfType<ImageBlock>());
        Assert.Equal("image/png", block.MimeType);
        // The image went through the attachment store and reads back.
        var store = new AttachmentService(_attRoot);
        var read = await store.ReadAsync(block.AttachmentId);
        Assert.NotNull(read);
        Assert.Equal(6, read!.Data.Length);
    }

    [Fact]
    public async Task Expand_TruncatesOverCap_AndDeduplicates()
    {
        var big = await Expand("all of @big.txt");
        var attachment = Assert.Single(big.Attached, a => a.Kind == "text");
        Assert.NotNull(attachment.Note);
        Assert.Contains("truncated", Assert.IsType<TextBlock>(big.Blocks[1]).Text);

        var twice = await Expand("@notes.txt and @notes.txt again");
        Assert.Equal(2, twice.Blocks.Count); // second occurrence deduped, no new block
        Assert.Single(twice.Attached);
    }

    [Fact]
    public async Task Expand_NoReferences_PlainText()
    {
        var result = await Expand("just talking, no files");
        Assert.Single(result.Blocks);
        Assert.Empty(result.Attached);
        Assert.IsType<TextBlock>(result.Blocks[0]);
    }

    [Fact]
    public void ListCandidates_RanksAndSkipsJunk()
    {
        var all = FileReferences.ListCandidates(_root, "s", max: 10);
        Assert.Contains(all, c => c.Path == "src/");               // directories are offered for drill-in
        Assert.Contains(all, c => c.Path == "notes.txt");
        Assert.DoesNotContain(all, c => c.Path.Contains("node_modules"));

        var exact = FileReferences.ListCandidates(_root, "Program.cs");
        Assert.Equal("src/Program.cs", exact[0].Path);
        Assert.False(exact[0].IsDir);

        Assert.Empty(FileReferences.ListCandidates(_root, ""));
        Assert.Empty(FileReferences.ListCandidates(Path.Combine(_root, "absent"), "x"));
    }
}
