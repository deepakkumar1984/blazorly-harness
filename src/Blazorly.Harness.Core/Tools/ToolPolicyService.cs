using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Tools;

/// <summary>
/// ctx.toolPolicy — per-session tool permission policy. <c>ask</c> mode routes every tool
/// call of that agent through the approval seam (PreToolDecision.Asked), which the front
/// end (web park / ACP request_permission) answers. Default is auto-allow.
/// </summary>
public sealed class ToolPolicyService
{
    public const string ServiceKey = "toolPolicy";

    private readonly HarnessContext _ctx;
    private readonly HashSet<string> _askEveryTool = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private ToolPolicyService(HarnessContext ctx) => _ctx = ctx;

    public static ToolPolicyService Mount(HarnessContext ctx)
    {
        var service = new ToolPolicyService(ctx);
        ctx.Provide(ServiceKey, service);
        var subscription = ctx.OnWaterfall<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute",
            (execution, decision, next, _) =>
            {
                if (decision.Kind != PreToolDecision.Allow) return next(decision);
                var agent = execution.Input.Agent;
                if (agent is null || !service.IsAskEveryTool(agent.Id)) return next(decision);
                return Task.FromResult(PreToolDecision.Asked("the session's permission mode is ask"));
            });
        ctx.Effect(() => subscription.Dispose());
        return service;
    }

    public void SetAskEveryTool(string agentId, bool askAll)
    {
        lock (_gate)
        {
            if (askAll) _askEveryTool.Add(agentId);
            else _askEveryTool.Remove(agentId);
        }
    }

    public bool IsAskEveryTool(string agentId)
    {
        lock (_gate) return _askEveryTool.Contains(agentId);
    }
}
