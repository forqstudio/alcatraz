namespace Alcatraz.Cli.Common.Bootstrap;

internal sealed class CancellationContext : IDisposable
{
    private readonly CancellationTokenSource cts = new();

    public CancellationContext()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public CancellationToken Token => cts.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        cts.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        cts.Dispose();
    }
}
