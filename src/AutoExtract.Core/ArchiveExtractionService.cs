using System.Collections.Concurrent;
using System.IO.Compression;

namespace AutoExtract.Core;

public sealed class ArchiveExtractionService
{
    private readonly IArchiveEngine engine;
    private readonly IFileRecycleService recycle;
    private readonly SettingsService storage;
    private readonly ArchiveDetectionService detection = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> failed = new(StringComparer.OrdinalIgnoreCase);
    public AppState State { get; }
    public event Action<JobRecord>? Recorded;
    public event Action? HistoryCleared;
    public Task ClearHistoryAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        lock (stateLock)
        {
            var previous = State.History;
            State.History = [];
            try { storage.Save("state.json", State); }
            catch { State.History = previous; throw; }
            // Queue UI updates in the same order as persisted Record notifications.
            HistoryCleared?.Invoke();
        }
    }, token);
    public void ResetFailures() => failed.Clear(); // call only while the watcher is stopped
    public ArchiveExtractionService(IArchiveEngine engine, IFileRecycleService recycle, SettingsService storage)
    {
        this.engine = engine; this.recycle = recycle; this.storage = storage;
        State = storage.Load<AppState>("state.json");
        State.Completed = new(State.Completed, StringComparer.OrdinalIgnoreCase);
    }
    public bool IsExcluded(string path) => path.Split(Path.DirectorySeparatorChar).Any(p => p.Equals(".AutoExtract-Working", StringComparison.OrdinalIgnoreCase)) || State.OutputFolders.Any(p => IsWithin(path, p));
    private static bool IsWithin(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public async Task ProcessAsync(string path, bool recycleOriginal, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (!detection.IsArchive(path) || !pending.TryAdd(path, 0)) return;
        var acquired = false;
        var leases = new List<SourceLease>();
        string signature = "", workspace = "", output = "";
        bool committed = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); acquired = true;
            if (IsExcluded(path) || !File.Exists(path)) return;
            ArchiveDetectionService.RejectReparseAncestors(path);
            var family = detection.Family(path);
            if (family.Length == 0 || detection.HasTemporaryCompanion(family)) return;
            try { foreach (var member in family) { ArchiveDetectionService.RejectReparseAncestors(member); leases.Add(new SourceLease(member)); } }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode is 32 or 33) { return; } // Open writer/reader: next sweep retries.
            signature = await ArchiveDetectionService.SignatureAsync(leases, token).ConfigureAwait(false);
            if (State.Completed.TryGetValue(path, out var done) && done == signature || failed.TryGetValue(path, out var bad) && bad == signature) return;
            var workRoot = Path.Combine(Path.GetDirectoryName(path)!, ".AutoExtract-Working");
            Directory.CreateDirectory(workRoot);
            HideWorkingRoot(workRoot);
            workspace = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            ArchiveDetectionService.RejectReparseAncestors(workspace);
            var payload = Path.Combine(workspace, "payload"); Directory.CreateDirectory(payload);
            var input = Path.Combine(workspace, "input"); Directory.CreateDirectory(input);
            // Work from private snapshots: some CLI versions do not share DELETE access,
            // while the original handles must retain DELETE rights for race-free recycling.
            foreach (var lease in leases)
            {
                lease.Stream.Position = 0;
                await using var copy = new FileStream(Path.Combine(input, Path.GetFileName(lease.Path)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                await lease.Stream.CopyToAsync(copy, token).ConfigureAwait(false);
                await copy.FlushAsync(token).ConfigureAwait(false);
            }
            var snapshot = Path.Combine(input, Path.GetFileName(path));
            PreflightZip(snapshot);
            await engine.TestAndExtractAsync(snapshot, payload, workspace, () => CheckFamily(path, family), token).ConfigureAwait(false);
            CheckFamily(path, family);
            if (signature != await ArchiveDetectionService.SignatureAsync(leases, token).ConfigureAwait(false)) throw new IOException("압축 해제 중 원본 변경 감지");
            var copies = new List<SourceLease>();
            try
            {
                foreach (var member in family)
                {
                    var copyPath = Path.Combine(input, Path.GetFileName(member));
                    ArchiveDetectionService.RejectReparseAncestors(copyPath);
                    copies.Add(new SourceLease(copyPath));
                }
                if (signature != await ArchiveDetectionService.SignatureAsync(copies, token).ConfigureAwait(false)) throw new IOException("작업 중 입력 복사본 변경 감지. 원본 보존.");
            }
            finally { foreach (var copy in copies) copy.Dispose(); }
            ValidateOutput(payload);
            token.ThrowIfCancellationRequested();
            output = Publish(payload, Path.GetDirectoryName(path)!, detection.BaseName(path));
            // Durable receipt BEFORE touching originals. A crash here retains originals and output.
            lock (stateLock)
            {
                State.OutputFolders.Add(output);
                State.Completed[path] = signature;
                storage.Save("state.json", State);
            }
            committed = true;
            var warnings = new List<string>();
            if (recycleOriginal)
            {
                // Private same-volume quarantine, renamed by the still-locked tested file handle.
                var originals = Path.Combine(workspace, "originals"); Directory.CreateDirectory(originals);
                foreach (var lease in leases)
                {
                    var quarantined = Path.Combine(originals, Path.GetFileName(lease.Path));
                    try
                    {
                        lease.MoveByHandle(quarantined);
                        lease.Dispose();
                        try { recycle.Recycle(quarantined); }
                        catch (Exception e)
                        {
                            try { File.Move(quarantined, lease.Path); }
                            catch { warnings.Add("원본 복구 경로: " + quarantined); }
                            warnings.Add("휴지통 이동 실패: " + e.Message);
                        }
                    }
                    catch (Exception e) { warnings.Add("원본 유지: " + e.Message); }
                }
            }
            if (warnings.Count == 0)
            {
                try { CleanupSuccessfulWorkspace(workspace, Path.GetDirectoryName(path)!); }
                catch (Exception e) { warnings.Add("작업 폴더 정리 실패 (결과물 유지): " + e.Message + " | 작업 경로: " + workspace); }
            }
            if (warnings.Count > 0)
            {
                var cleanupWarning = CleanupRetainingLogs(workspace, Path.GetDirectoryName(path)!);
                if (cleanupWarning.Length > 0) warnings.Add(cleanupWarning);
            }
            Record(new(path, signature, DateTimeOffset.Now, warnings.Count == 0 ? "성공" : "성공 · 경고", warnings.Count == 0 ? "압축 해제 완료" : string.Join(" / ", warnings), output));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (acquired) Record(new(path, signature, DateTimeOffset.Now, "취소", "프로그램 종료/일시정지. 원본 보존. 로그 경로: " + workspace + CleanupRetainingLogs(workspace, Path.GetDirectoryName(path)!), committed ? output : null)); }
        catch (Exception e)
        {
            if (signature.Length > 0 && !committed) failed[path] = signature;
            var cleanupWarning = CleanupRetainingLogs(workspace, Path.GetDirectoryName(path)!);
            Record(new(path, signature, DateTimeOffset.Now, committed ? "성공 · 경고" : "실패", e.Message + (workspace.Length > 0 ? " | 로그 경로: " + workspace : "") + cleanupWarning, output.Length > 0 ? output : null));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            if (acquired) gate.Release(); pending.TryRemove(path, out _);
        }
    }
    private void CheckFamily(string path, string[] family)
    {
        if (!family.SequenceEqual(detection.Family(path), StringComparer.OrdinalIgnoreCase) || detection.HasTemporaryCompanion(family)) throw new IOException("분할 구성 또는 임시 다운로드 파일 변경. 원본 보존.");
    }
    private static string CleanupRetainingLogs(string workspace, string archiveParent)
    {
        if (workspace.Length == 0 || !Directory.Exists(workspace)) return "";
        try { CleanupSuccessfulWorkspace(workspace, archiveParent, retainLogs: true); return ""; }
        catch (Exception e) { return " | 임시 데이터 정리 실패: " + e.Message; }
    }
    private static void CleanupSuccessfulWorkspace(string workspace, string archiveParent, bool retainLogs = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var root = Path.Combine(Path.GetFullPath(archiveParent), ".AutoExtract-Working");
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new IOException("예상하지 않은 작업 폴더 경로. 정리하지 않습니다.");
        ArchiveDetectionService.RejectReparseAncestors(full);
        var knownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "input", "scratch", "originals" };
        if (retainLogs) knownFolders.Add("payload");
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bz-t.log", "bz-l.log", "bz-x.log" };
        var files = new List<string>();
        var directories = new List<string>();
        // Validate the entire deletion plan before deleting anything. Never follow links,
        // remove quarantined originals, touch the published output, or recurse into siblings.
        void Inspect(string directory, bool topLevel)
        {
            ArchiveDetectionService.RejectReparseAncestors(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("작업 폴더에 링크가 있어 자동 정리를 중단합니다.");
                var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (topLevel && !knownFolders.Contains(name)) throw new IOException("예상하지 않은 작업 폴더 항목: " + name);
                    if (topLevel && name.Equals("originals", StringComparison.OrdinalIgnoreCase) && Directory.EnumerateFileSystemEntries(entry).Any())
                    {
                        if (retainLogs) continue;
                        throw new IOException("복구할 원본이 남아 있어 작업 폴더를 보존합니다.");
                    }
                    Inspect(entry, false);
                    directories.Add(entry);
                }
                else
                {
                    if (topLevel && !knownFiles.Contains(name)) throw new IOException("예상하지 않은 작업 파일: " + name);
                    if (topLevel && retainLogs) continue;
                    files.Add(entry);
                }
            }
        }
        Inspect(full, true);
        foreach (var file in files) { ArchiveDetectionService.RejectReparseAncestors(file); File.Delete(file); }
        // Nonrecursive deletes fail safely if another file appears during cleanup.
        foreach (var directory in directories) { ArchiveDetectionService.RejectReparseAncestors(directory); Directory.Delete(directory, false); }
        if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full, false);
        TryRemoveEmptyWorkingRoot(root);
    }
    private static void TryRemoveEmptyWorkingRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        ArchiveDetectionService.RejectReparseAncestors(root);
        if (Directory.EnumerateFileSystemEntries(root).Any()) return;
        try { Directory.Delete(root, false); }
        catch (IOException) when (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) { } // Another job appeared.
    }
    private void Record(JobRecord record)
    {
        lock (stateLock)
        {
            State.History.Insert(0, record);
            if (State.History.Count > 500) State.History.RemoveRange(500, State.History.Count - 500);
            try { storage.Save("state.json", State); }
            catch (Exception e) { record = record with { Detail = record.Detail + " | 기록 저장 실패: " + e.Message }; }
            Recorded?.Invoke(record);
        }
    }
    public void ReportError(string path, Exception error) => Recorded?.Invoke(new(path, "", DateTimeOffset.Now, "감시 오류", error.Message));
    private static void HideWorkingRoot(string root)
    {
        ArchiveDetectionService.RejectReparseAncestors(root);
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.Hidden) == 0)
            File.SetAttributes(root, attributes | FileAttributes.Hidden);
    }
    public void RecoverOriginals(string parent)
    {
        var root = Path.Combine(parent, ".AutoExtract-Working");
        if (!Directory.Exists(root)) return;
        HideWorkingRoot(root);
        foreach (var work in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(work), "N", out _)) continue;
            ArchiveDetectionService.RejectReparseAncestors(work);
            var originals = Path.Combine(work, "originals");
            if (!Directory.Exists(originals))
            {
                var warning = CleanupRetainingLogs(work, parent);
                if (warning.Length > 0) ReportError(work, new IOException(warning));
                continue;
            }
            ArchiveDetectionService.RejectReparseAncestors(originals);
            foreach (var source in Directory.EnumerateFiles(originals))
            {
                try
                {
                    ArchiveDetectionService.RejectReparseAncestors(source);
                    var target = Path.Combine(parent, Path.GetFileName(source));
                    File.Move(source, target); // Atomic, never overwrite a replacement/download.
                    Record(new(target, "", DateTimeOffset.Now, "복구", "중단된 휴지통 작업의 원본을 복구했습니다."));
                }
                catch (Exception e) { ReportError(source, new IOException("보존된 원본 수동 복구 필요: " + source + " | " + e.Message)); }
            }
            var cleanupWarning = CleanupRetainingLogs(work, parent);
            if (cleanupWarning.Length > 0) ReportError(work, new IOException(cleanupWarning));
        }
        TryRemoveEmptyWorkingRoot(root);
    }
    public static string Publish(string payload, string parent, string name)
    {
        for (int index = 1; ; index++)
        {
            var dest = Path.Combine(parent, index == 1 ? name : $"{name} ({index})");
            try { Directory.Move(payload, dest); return dest; }
            catch (IOException) when (File.Exists(dest) || Directory.Exists(dest)) { }
        }
    }
    private static void PreflightZip(string path)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return;
        // Split ZIPs require Bandizip; confinement remains in force for all formats.
        if (File.Exists(Path.ChangeExtension(path, ".z01"))) return;
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is ".." || p.EndsWith('.') || p.EndsWith(' ')) || name.Any(char.IsControl)) throw new InvalidDataException("안전하지 않은 ZIP 경로: " + name);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ZIP 링크 항목 거부: " + name);
        }
    }
    private static void ValidateOutput(string root)
    {
        if (!Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("압축 해제 결과가 비어 있습니다. 원본 보존.");
        var dirs = new Stack<string>(); dirs.Push(root);
        while (dirs.TryPop(out var dir))
        {
            ArchiveDetectionService.RejectReparseAncestors(dir);
            foreach (var p in Directory.EnumerateFileSystemEntries(dir))
            {
                var attrs = File.GetAttributes(p);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("추출 결과의 링크/재분석 항목 거부: " + p);
                if ((attrs & FileAttributes.Directory) != 0) dirs.Push(p);
            }
        }
    }
}
