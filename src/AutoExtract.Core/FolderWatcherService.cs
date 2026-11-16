using System.Threading.Channels;

namespace AutoExtract.Core;

public sealed class FolderWatcherService : IAsyncDisposable
{
    private readonly ArchiveExtractionService extraction;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource? cancellation;
    private Task? loop;
    public FolderWatcherService(ArchiveExtractionService extraction) => this.extraction = extraction;
    public async Task StartAsync(AppSettings settings)
    {
        await StopAsync();
        extraction.ResetFailures();
        if (settings.Paused) return;
        cancellation = new();
        var folders = settings.Folders.Where(f => f.Enabled).Select(f => new WatchFolder { Path = Path.GetFullPath(f.Path), IncludeSubdirectories = f.IncludeSubdirectories }).ToArray();
        loop = Task.Run(() => RunAsync(folders, settings.RecycleOriginal, cancellation.Token));
    }
    private async Task RunAsync(WatchFolder[] folders, bool recycle, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Recreate failed/disconnected watchers on every sweep; scanning remains authoritative.
                foreach (var folder in folders)
                {
                    try
                    {
                        if (!Directory.Exists(folder.Path)) throw new DirectoryNotFoundException("감시 폴더가 없거나 연결 해제됨");
                        if (!watchers.Any(w => w.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase) && w.EnableRaisingEvents))
                        {
                            foreach (var old in watchers.Where(w => w.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) { old.Dispose(); watchers.Remove(old); }
                            var watcher = new FileSystemWatcher(folder.Path) { IncludeSubdirectories = folder.IncludeSubdirectories, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName };
                            FileSystemEventHandler changed = (_, _) => wake.Writer.TryWrite(true);
                            watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed;
                            watcher.Renamed += (_, _) => wake.Writer.TryWrite(true);
                            watcher.Error += (_, e) => { extraction.ReportError(folder.Path, e.GetException()); watcher.EnableRaisingEvents = false; wake.Writer.TryWrite(true); };
                            watcher.EnableRaisingEvents = true; watchers.Add(watcher);
                        }
                        await ScanAsync(folder.Path, folder.IncludeSubdirectories, recycle, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception e) { extraction.ReportError(folder.Path, e); }
                }
                // Drain coalesced events, then wait with a sweep timeout; no unbounded event queue.
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(token); tick.CancelAfter(TimeSpan.FromSeconds(10));
                try { await wake.Reader.ReadAsync(tick.Token); } catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task ScanAsync(string root, bool recursive, bool recycle, CancellationToken token)
    {
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.TryPop(out var dir))
        {
            token.ThrowIfCancellationRequested();
            if (extraction.IsExcluded(dir)) continue;
            try
            {
                ArchiveDetectionService.RejectReparseAncestors(dir);
                extraction.RecoverOriginals(dir);
                foreach (var file in Directory.EnumerateFiles(dir)) { token.ThrowIfCancellationRequested(); await extraction.ProcessAsync(file, recycle, token); }
                if (recursive) foreach (var sub in Directory.EnumerateDirectories(dir)) if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) { extraction.ReportError(dir, e); }
        }
    }
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (loop != null) await loop.ConfigureAwait(false);
        foreach (var w in watchers) w.Dispose(); watchers.Clear();
        cancellation?.Dispose(); cancellation = null; loop = null;
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
