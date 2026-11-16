namespace AutoExtract.Core;

public sealed class BandizipEngine(Func<string> configuredPath) : IArchiveEngine
{
    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) && Path.GetFileName(configured).Equals("bz.exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(configured) : null;
        return new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") }
            .Select(p => Path.Combine(p, "Bandizip", "bz.exe")).FirstOrDefault(File.Exists);
    }
    public Task TestAsync(string archive, string workspace, CancellationToken token) => InvokeAsync(["t", "-y", "-consolemode:utf8", "-p:", "-", archive], workspace, token);
    public async Task ExtractAsync(string archive, string destination, string workspace, CancellationToken token)
    {
        await InvokeAsync(["l", "-list:s", "-consolemode:utf8", "-p:", "-", archive], workspace, token).ConfigureAwait(false);
        await PrepareDirectoriesAsync(Path.Combine(workspace, "bz-l.log"), destination, token).ConfigureAwait(false);
        await InvokeAsync(["x", "-y", "-aou", "-consolemode:utf8", "-p:", "-o:" + destination, "-", archive], workspace, token, destination).ConfigureAwait(false);
    }
    private static async Task PrepareDirectoriesAsync(string listing, string destination, CancellationToken token)
    {
        // Bandizip's directory-creation helper can fail inside AppContainer. Create only
        // validated in-workspace directories ourselves; extraction remains sandboxed.
        if (new FileInfo(listing).Length > 64L * 1024 * 1024) throw new InvalidDataException("압축 항목 목록이 안전 처리 한도(64MiB)를 초과했습니다. 원본 보존.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        ArchiveDetectionService.RejectReparseAncestors(root);
        using var reader = new StreamReader(listing, System.Text.Encoding.UTF8);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } entry)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length == 0) continue;
            if (entry.Length > 32700 || Path.IsPathRooted(entry) || entry.Contains(':') || entry.Any(char.IsControl)) throw new InvalidDataException("안전하지 않은 압축 항목 경로. 원본 보존.");
            var parts = entry.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part == ".") continue;
                var stem = part.Split('.')[0];
                var reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                    || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                    || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (part == ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || reserved)
                    throw new InvalidDataException("안전하지 않은 압축 항목 경로. 원본 보존.");
            }
            var target = Path.GetFullPath(Path.Combine(root, entry));
            if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("추출 폴더 밖의 압축 항목 거부.");
            var folder = entry.EndsWith('/') || entry.EndsWith('\\') ? target : Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            ArchiveDetectionService.RejectReparseAncestors(folder);
        }
    }
    private async Task InvokeAsync(string[] args, string workspace, CancellationToken token, string? writableDirectory = null)
    {
        var exe = FindExecutable(configuredPath()) ?? throw new FileNotFoundException("반디집 bz.exe가 없습니다. 반디집을 설치하거나 설정에서 경로를 지정하세요.");
        // Watchdog is a hung-process guard, never a download-completion heuristic.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromHours(12));
        try
        {
            var logPath = Path.Combine(workspace, $"bz-{args[0]}.log");
            var code = await RestrictedProcess.RunAsync(exe, args, workspace, timeout.Token, logPath, writableDirectory ?? Path.Combine(workspace, "scratch")).ConfigureAwait(false);
            if (code != 0)
            {
                var detail = ReadLogTail(logPath);
                throw new InvalidDataException($"반디집 {args[0]} 실패 (종료 코드 {code}). 원본 보존. {detail} | 상세 로그: {logPath}");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("반디집 응답 제한 시간(12시간) 초과. 원본 보존."); }
    }
    private static string ReadLogTail(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (file.Length > 16384) file.Seek(-16384, SeekOrigin.End);
            using var reader = new StreamReader(file, System.Text.Encoding.UTF8);
            var lines = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var error = lines.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("오류") || line.Contains("실패")).TakeLast(3);
            var summary = string.Join(" / ", error);
            return summary.Length > 0 ? summary[..Math.Min(summary.Length, 1200)] : "상세 로그를 확인하세요.";
        }
        catch { return "상세 로그 읽기 실패."; }
    }
}
