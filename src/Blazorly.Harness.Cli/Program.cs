using Blazorly.Harness.Cli;

// blazorly — the product launcher (dsh apps/cli parity).
//   blazorly run "job"       one headless task over the invoking directory
//   blazorly sessions        list persisted sessions
//
// Exit codes for `run`: 0 completed/max-tokens · 2 turn error/blocked · 3 aborted · 1 failure.

return args.Length == 0 ? Help() : args[0] switch
{
    "run" => await RunAsync(args[1..]),
    "sessions" => await SessionsAsync(args[1..]),
    "serve-stdio" => await ServeStdioAsync(args[1..]),
    "serve-acp" => await ServeAcpAsync(args[1..]),
    "--help" or "-h" or "help" => Help(),
    var unknown => Unknown(unknown),
};

static int Help()
{
    Console.WriteLine("""
        blazorly — agentic coding harness (blazorly-harness CLI)

        Commands:
          run "job"        Run one headless task. The invoking directory becomes the
                           session workspace on first use. Streams the assistant text.
                           Flags:
                             --workspace <path>   workspace root (default: current dir)
                             --provider <name>    replay | deepseek | openai | anthropic |
                                                  openai-compatible | a configured custom route
                             --model <id>         model id for the run
                             --resume <id>        continue a persisted session
                             --timeout <seconds>  cancel the run after N seconds (exit 3)
                             --json               print one JSON envelope instead of the stream
                             --quiet              suppress streamed output
          sessions         List persisted sessions (newest last). Flag:
                             --workspace <path>   only sessions for this root
          serve-stdio      Serve the JSON-RPC automation protocol on stdin/stdout
                           (initialize, session/new, session/prompt, session/cancel,
                           shutdown; session.event + session.status notifications).
                           Flag:
                             --workspace <path>   default session workspace
          serve-acp        Serve the Agent Client Protocol (ACP) on stdin/stdout for
                           editors and SDK clients (initialize, session/new,
                           session/prompt, session/load, session/cancel,
                           session/set_config_option; session/update notifications,
                           session/request_permission in ask mode). Flags:
                             --workspace <path>     default session workspace
                             --chunk-delay <ms>     replay-provider pacing (tests/demos)
                             --permission <mode>    auto (default) or ask: route every
                                                    tool call to the client as a
                                                    session/request_permission prompt

        Provider keys resolve from ~/.blazorly/settings.json, then the provider's environment
        variable (DEEPSEEK_API_KEY / OPENAI_API_KEY / ANTHROPIC_API_KEY). The `replay`
        provider runs a scripted demo agent with no key.

        Exit codes (run): 0 completed · 2 error · 3 aborted · 1 harness failure.
        """);
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}' — try `blazorly --help`");
    return 1;
}

static (HeadlessOptions Options, List<string> Positional) Parse(string[] args)
{
    var options = new HeadlessOptions();
    var positional = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--workspace" when i + 1 < args.Length:
                options = options with { WorkspacePath = args[++i] };
                break;
            case "--provider" when i + 1 < args.Length:
                options = options with { Provider = args[++i] };
                break;
            case "--model" when i + 1 < args.Length:
                options = options with { Model = args[++i] };
                break;
            case "--resume" when i + 1 < args.Length:
                options = options with { ResumeSessionId = args[++i] };
                break;
            case "--timeout" when i + 1 < args.Length && int.TryParse(args[++i], out var seconds):
                options = options with { TimeoutSeconds = seconds };
                break;
            case "--chunk-delay" when i + 1 < args.Length && int.TryParse(args[++i], out var delayMs):
                options = options with { ChunkDelayMs = delayMs };
                break;
            case "--permission" when i + 1 < args.Length:
                options = options with { Permission = args[++i] };
                break;
            case "--json":
                options = options with { Json = true };
                break;
            case "--quiet":
                options = options with { Quiet = true };
                break;
            default:
                positional.Add(args[i]);
                break;
        }
    }
    return (options, positional);
}

static async Task<int> RunAsync(string[] args)
{
    var (options, positional) = Parse(args);
    var job = string.Join(' ', positional);
    if (string.IsNullOrWhiteSpace(job))
    {
        Console.Error.WriteLine("usage: blazorly run \"job text\" [--workspace <path>] [--provider <p>] [--model <m>] [--resume <id>] [--timeout <s>] [--json] [--quiet]");
        return 1;
    }
    var result = await HeadlessRunner.RunAsync(options with { Job = job });
    if (result.Error is not null) Console.Error.WriteLine($"error: {result.Error}");
    return result.ExitCode;
}

static async Task<int> SessionsAsync(string[] args)
{
    var (options, positional) = Parse(args);
    return await HeadlessRunner.ListSessionsAsync(options);
}

static async Task<int> ServeStdioAsync(string[] args)
{
    var (options, _) = Parse(args);
    Console.Error.WriteLine("[blazorly] jsonrpc server on stdio; logs here");
    return await JsonRpcServer.RunAsync(
        new JsonRpcServerOptions { WorkspacePath = options.WorkspacePath },
        Console.In,
        Console.Out,
        Console.Error,
        CancellationToken.None);
}

static async Task<int> ServeAcpAsync(string[] args)
{
    var (options, _) = Parse(args);
    Console.Error.WriteLine("[blazorly] acp server on stdio; logs here");
    return await AcpServer.RunAsync(
        new AcpServerOptions
        {
            WorkspacePath = options.WorkspacePath,
            ChunkDelayMs = options.ChunkDelayMs,
            Permission = options.Permission ?? "auto",
        },
        Console.In,
        Console.Out,
        Console.Error,
        CancellationToken.None);
}
