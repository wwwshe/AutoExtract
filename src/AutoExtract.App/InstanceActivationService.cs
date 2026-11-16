using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoExtract.App;

/// <summary>Ask the existing per-user process to show its window, without starting another watcher.</summary>
public sealed class InstanceActivationService : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Func<Task> showWindow;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task listener;
    public InstanceActivationService(string pipeName, Func<Task> showWindow)
    {
        this.pipeName = pipeName;
        this.showWindow = showWindow;
        listener = Task.Run(ListenAsync);
    }
    public static string NameForUser(string sid) => "AutoExtract.Activate." + sid;
    public static async Task<bool> ShowExistingAsync(string pipeName, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, true);
            var greeting = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (!int.TryParse(greeting, out var processId) || processId <= 0) return false;
            // The newly launched process can transfer its foreground activation permission
            // to the existing process before asking it to activate the WPF window.
            AllowSetForegroundWindow((uint)processId);
            await pipe.WriteAsync("SHOW\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) == "OK";
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 256, 256);
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString().AsMemory(), request.Token).ConfigureAwait(false);
                if (await reader.ReadLineAsync(request.Token).ConfigureAwait(false) != "SHOW") continue;
                await showWindow().WaitAsync(request.Token).ConfigureAwait(false);
                await writer.WriteLineAsync("OK".AsMemory(), request.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // Disconnected/invalid clients must not stop subsequent activation requests.
                if (!lifetime.IsCancellationRequested)
                    try { await Task.Delay(50, lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }
    public void Stop() { if (!lifetime.IsCancellationRequested) lifetime.Cancel(); }
    public async ValueTask DisposeAsync() { Stop(); await listener.ConfigureAwait(false); lifetime.Dispose(); }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
