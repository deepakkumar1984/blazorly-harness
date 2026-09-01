namespace Blazorly.Harness.Kernel;

internal sealed class Disposables(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

public static class Disposable
{
    public static IDisposable Of(Action dispose) => new Disposables(dispose);
}
