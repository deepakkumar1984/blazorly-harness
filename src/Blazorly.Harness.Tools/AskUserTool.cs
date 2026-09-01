using System.Text;
using System.Text.Json.Serialization;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record AskUserOptionInput(string Label, string? Description = null);

public sealed record AskUserQuestionInput(
    string Id,
    string Question,
    string? Header = null,
    IReadOnlyList<AskUserOptionInput>? Options = null,
    [property: JsonPropertyName("multi_select")] bool? MultiSelect = null);

public sealed record AskUserArgs(IReadOnlyList<AskUserQuestionInput> Questions);

public sealed record AskUserAnswerView(string Id, string Text);

public sealed record AskUserOutput(IReadOnlyList<AskUserAnswerView> Answers);

/// <summary>ask_user_question: pauses the turn until the human answers through ctx.userQuestions.</summary>
public sealed class AskUserTool(HarnessContext ctx) : ToolDefinition<AskUserArgs, AskUserOutput>
{
    public const string NoProviderCode = "NO_USER_QUESTIONS_PROVIDER";

    public override string Name => "ask_user_question";

    public override string Description =>
        "Ask the user a concise question when you need confirmation, a choice, or missing information "
        + "before proceeding. Send one or more questions, each with a stable id that will be echoed in the answer.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["questions"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                AdditionalProperties = true,
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["id"] = JsonSchema.String("Stable id for this question; echoed in the answer."),
                    ["question"] = JsonSchema.String("The specific question to ask the user."),
                    ["header"] = JsonSchema.String("Optional short heading for the question, such as \"Confirm\" or \"Choose Mode\"."),
                    ["options"] = JsonSchema.Array(new JsonSchema.Schema
                    {
                        Type = "object",
                        AdditionalProperties = true,
                        Properties = new Dictionary<string, JsonSchema.Schema>
                        {
                            ["label"] = JsonSchema.String("Short user-facing option label."),
                            ["description"] = JsonSchema.String("One sentence explaining the tradeoff or impact."),
                        },
                        Required = ["label"],
                    }, "Optional choices to show the user. If you recommend one, put it first and append \"(Recommended)\" to that label."),
                    ["multi_select"] = JsonSchema.Boolean("Whether the user may select more than one option. Defaults to false."),
                },
                Required = ["id", "question"],
            }, "Questions to ask the user before continuing."),
        },
        required: ["questions"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["answers"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["id"] = JsonSchema.String(),
                    ["text"] = JsonSchema.String(),
                },
                Required = ["id", "text"],
                AdditionalProperties = false,
            }),
        },
        required: ["answers"]);

    public override int? TimeoutMs => 600_000;

    protected override async Task<AskUserOutput> ExecuteTyped(AskUserArgs args, ToolRunContext exec)
    {
        var lookup = exec.Agent?.Ctx ?? ctx;
        var service = lookup.TryGet<UserQuestionsService>(UserQuestionsService.ServiceKey)
            ?? throw new ToolException(NoProviderCode, "no user interface is available to answer questions");
        var questions = args.Questions
            .Select(q => new AskQuestion(q.Id, q.Question, q.Header,
                q.Options is null ? null : [.. q.Options.Select(o => new AskOption(o.Label, o.Description))],
                q.MultiSelect ?? false))
            .ToList();
        var answers = await service.AskAsync(questions, exec.Signal).ConfigureAwait(false);
        return new AskUserOutput([.. answers.Select(a => new AskUserAnswerView(a.Id, a.Text))]);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(AskUserArgs args, AskUserOutput output)
    {
        var builder = new StringBuilder();
        foreach (var answer in output.Answers) builder.Append(answer.Id).Append(": ").AppendLine(answer.Text);
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(AskUserArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Ask the user",
        Description = args.Questions.Count == 1 ? args.Questions[0].Question : $"{args.Questions.Count} questions",
    };
}

/// <summary>Mounts the model-facing ask_user_question tool over the userQuestions seam.</summary>
public sealed class AskUserPlugin : HarnessPlugin
{
    public override string Name => "ask-user";
    public override string[] Inject { get; } = ["tools"];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new AskUserTool(ctx)).Dispose);
        return Task.CompletedTask;
    }
}
