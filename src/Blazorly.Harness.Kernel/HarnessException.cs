namespace Blazorly.Harness.Kernel;

/// <summary>Base exception carrying a stable machine-readable code, mirroring dsh's HarnessError.</summary>
public class HarnessException : Exception
{
    public string Code { get; }

    public HarnessException(string code, string message) : base(message) => Code = code;

    public HarnessException(string code, string message, Exception inner) : base(message, inner) => Code = code;
}
