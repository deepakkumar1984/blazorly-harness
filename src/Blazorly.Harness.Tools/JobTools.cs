using System.Text;
using Blazorly.Harness.Core.Jobs;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record JobListArgs;

public sealed record JobSummary(string Id, string Kind, string Description, string Status, int? ExitCode, DateTimeOffset StartedAt);

public sealed record JobListOutput(IReadOnlyList<JobSummary> Jobs);

/// <summary>job_list: every background job with kind, status, and exit code.</summary>
public sealed class JobListTool(JobsRuntime jobs) : ToolDefinition<JobListArgs, JobListOutput>
{
    public override string Name => "job_list";

    public override string Description =>
        "List background jobs started in this session (background bash, terminals, subagents) with id, "
        + "kind, description, status, and exit code. Use job_output to read a job's output.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["jobs"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["id"] = JsonSchema.String(),
                    ["kind"] = JsonSchema.String(),
                    ["description"] = JsonSchema.String(),
                    ["status"] = JsonSchema.String(),
                    ["exitCode"] = JsonSchema.Integer(),
                    ["startedAt"] = JsonSchema.String(),
                },
                Required = ["id", "kind", "description", "status", "startedAt"],
                AdditionalProperties = false,
            }),
        },
        required: ["jobs"]);

    protected override bool IsConcurrencySafeTyped(JobListArgs args) => true;

    protected override Task<JobListOutput> ExecuteTyped(JobListArgs args, ToolRunContext exec)
        => Task.FromResult(new JobListOutput(
            [.. jobs.List().Select(j => new JobSummary(j.Id, j.Kind, j.Description, j.Status, j.ExitCode, j.StartedAt))]));

    protected override IReadOnlyList<ContentBlock> RenderTyped(JobListArgs args, JobListOutput output)
    {
        if (output.Jobs.Count == 0) return [new TextBlock("No background jobs.")];
        var builder = new StringBuilder();
        foreach (var job in output.Jobs)
        {
            builder.Append(job.Id).Append(" [").Append(job.Status).Append("] ").Append(job.Kind)
                .Append(" — ").AppendLine(job.Description);
        }
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(JobListArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "List background jobs",
    };
}

public sealed record JobOutputArgs(
    [property: System.Text.Json.Serialization.JsonPropertyName("job_id")] string JobId,
    [property: System.Text.Json.Serialization.JsonPropertyName("tail_chars")] int? TailChars = null);

public sealed record JobOutputValue(string JobId, string Status, string Output);

/// <summary>job_output: the captured output of one background job, tailed.</summary>
public sealed class JobOutputTool(JobsRuntime jobs) : ToolDefinition<JobOutputArgs, JobOutputValue>
{
    private const int DefaultTailChars = 8000;

    public override string Name => "job_output";

    public override string Description =>
        "Read the output captured so far from a background job. Output keeps accumulating while the job runs; "
        + "tail_chars returns only the last N characters (default 8000).";

    public override int? TimeoutMs => 5000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["job_id"] = JsonSchema.String("Id of the job to read, e.g. job_1."),
            ["tail_chars"] = JsonSchema.Integer("Return only the last N characters of output. Defaults to 8000."),
        },
        required: ["job_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["jobId"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(),
            ["output"] = JsonSchema.String(),
        },
        required: ["jobId", "status", "output"]);

    protected override bool IsConcurrencySafeTyped(JobOutputArgs args) => true;

    protected override Task<JobOutputValue> ExecuteTyped(JobOutputArgs args, ToolRunContext exec)
    {
        var info = jobs.Get(args.JobId) ?? throw new ToolException("UNKNOWN_JOB", $"job '{args.JobId}' is not known");
        var output = jobs.ReadOutput(args.JobId, args.TailChars is > 0 ? args.TailChars.Value : DefaultTailChars) ?? "";
        return Task.FromResult(new JobOutputValue(info.Id, info.Status, output));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(JobOutputArgs args, JobOutputValue output)
    {
        var text = output.Output.TrimEnd();
        return [new TextBlock(text.Length > 0 ? text : $"(job {output.JobId} has no output yet)")];
    }

    protected override ToolCallView? PresentCallTyped(JobOutputArgs args) => new()
    {
        Card = "terminal",
        Kind = "read",
        Title = args.JobId,
        Description = "read job output",
    };
}

public sealed record JobKillArgs([property: System.Text.Json.Serialization.JsonPropertyName("job_id")] string JobId);

public sealed record JobKillValue(string JobId, bool Killed);

/// <summary>job_kill: stop a background job.</summary>
public sealed class JobKillTool(JobsRuntime jobs) : ToolDefinition<JobKillArgs, JobKillValue>
{
    public override string Name => "job_kill";

    public override string Description =>
        "Kill a background job by id. The job's process tree is terminated; its captured output stays readable.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["job_id"] = JsonSchema.String("Id of the job to kill, e.g. job_1."),
        },
        required: ["job_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["jobId"] = JsonSchema.String(),
            ["killed"] = JsonSchema.Boolean(),
        },
        required: ["jobId", "killed"]);

    protected override Task<JobKillValue> ExecuteTyped(JobKillArgs args, ToolRunContext exec)
        => Task.FromResult(new JobKillValue(args.JobId, jobs.KillJob(args.JobId)));

    protected override IReadOnlyList<ContentBlock> RenderTyped(JobKillArgs args, JobKillValue output)
        => [new TextBlock(output.Killed ? $"Job {output.JobId} killed." : $"No job '{output.JobId}' to kill.")];

    protected override ToolCallView? PresentCallTyped(JobKillArgs args) => new()
    {
        Card = "terminal",
        Kind = "delete",
        Title = args.JobId,
        Description = "kill background job",
    };
}

/// <summary>
/// Mounts the job_* tools over ctx.jobs. The orchestrator normally mounts the runtime;
/// this plugin mounts it itself when absent.
/// </summary>
public sealed class JobsPlugin : HarnessPlugin
{
    public override string Name => "jobs";
    public override string[] Inject { get; } = [ToolRuntime.ServiceKey];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var jobs = ctx.TryGet<JobsRuntime>(JobsRuntime.ServiceKey) ?? JobsRuntime.Mount(ctx);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
        ctx.Effect(tools.Register(new JobListTool(jobs)).Dispose);
        ctx.Effect(tools.Register(new JobOutputTool(jobs)).Dispose);
        ctx.Effect(tools.Register(new JobKillTool(jobs)).Dispose);
        return Task.CompletedTask;
    }
}
