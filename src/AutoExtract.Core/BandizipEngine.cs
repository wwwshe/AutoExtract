namespace AutoExtract.Core;

public sealed class BandizipEngine(Func<string> configuredPath, Func<IReadOnlyList<string>>? configuredPasswords = null) : IArchiveEngine
{
    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) && Path.GetFileName(configured).Equals("bz.exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(configured) : null;
        return new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") }
            .Select(p => Path.Combine(p, "Bandizip", "bz.exe")).FirstOrDefault(File.Exists);
    }
    public async Task TestAsync(string archive, string workspace, CancellationToken token) => await FindPasswordAsync(archive, workspace, token).ConfigureAwait(false);
    public async Task ExtractAsync(string archive, string destination, string workspace, CancellationToken token)
    {
        var password = await FindPasswordAsync(archive, workspace, token).ConfigureAwait(false);
        await ExtractWithPasswordAsync(archive, destination, workspace, password, token).ConfigureAwait(false);
    }
    public async Task TestAndExtractAsync(string archive, string destination, string workspace, Action afterTest, CancellationToken token)
    {
        var password = await FindPasswordAsync(archive, workspace, token).ConfigureAwait(false);
        afterTest();
        await ExtractWithPasswordAsync(archive, destination, workspace, password, token).ConfigureAwait(false);
    }
    private async Task<string> FindPasswordAsync(string archive, string workspace, CancellationToken token)
    {
        // Retry only an actual CLI nonzero exit, never cancellation, timeout or sandbox failures.
        BandizipExitException? last = null;
        var candidates = new[] { "" }.Concat(configuredPasswords?.Invoke() ?? []).Distinct(StringComparer.Ordinal);
        foreach (var password in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await InvokeAsync(["t", "-y", "-consolemode:utf8", "-p:" + password, "-", archive], workspace, token).ConfigureAwait(false);
                return password;
            }
            catch (BandizipExitException error) { last = error; }
        }
        throw new ArchiveIntegrityException("빈 암호와 저장된 암호로 무결성 검사를 통과하지 못했습니다. 암호 불일치·파일 손상·분할 누락 등을 확인하세요. " + last!.Message);
    }
    private async Task ExtractWithPasswordAsync(string archive, string destination, string workspace, string password, CancellationToken token)
    {
        await InvokeAsync(["l", "-consolemode:utf8", "-p:" + password, "-", archive], workspace, token).ConfigureAwait(false);
        await PrepareDirectoriesAsync(Path.Combine(workspace, "bz-l.log"), destination, token).ConfigureAwait(false);
        await InvokeAsync(["x", "-y", "-aou", "-consolemode:utf8", "-p:" + password, "-o:" + destination, "-", archive], workspace, token, destination).ConfigureAwait(false);
    }
    private static async Task PrepareDirectoriesAsync(string listing, string destination, CancellationToken token)
    {
        // Bandizip's directory-creation helper can fail inside AppContainer. Create only
        // validated in-workspace directories ourselves; extraction remains sandboxed.
        if (new FileInfo(listing).Length > 64L * 1024 * 1024) throw new InvalidDataException("압축 항목 목록이 안전 처리 한도(64MiB)를 초과했습니다. 원본 보존.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        ArchiveDetectionService.RejectReparseAncestors(root);
        using var reader = new StreamReader(listing, System.Text.Encoding.UTF8);
        var inTable = false;
        var complete = false;
        var nameOffset = 0;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            token.ThrowIfCancellationRequested();
            // The short listing omits trailing separators on RAR directories.
            // Read the directory attribute from the standard listing instead.
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^-{19} -{4} -{12} -{12} -+$"))
            {
                if (inTable) { complete = true; break; }
                nameOffset = line.LastIndexOf(' ') + 1;
                inTable = true;
                continue;
            }
            if (!inTable) continue;
            if (line.Length <= nameOffset || !System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [A-Z_]{4} +\d+ +\d+ "))
                throw new InvalidDataException("압축 항목 목록 형식을 인식할 수 없습니다. 원본 보존.");
            var entry = line[nameOffset..];
            var isDirectory = line[20] == 'D';
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
            var folder = isDirectory ? target : Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            ArchiveDetectionService.RejectReparseAncestors(folder);
        }
        if (!complete) throw new InvalidDataException("압축 항목 목록이 불완전합니다. 원본 보존.");
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
                // Do not copy CLI text into history: it may contain a password or command line.
                throw new BandizipExitException($"반디집 {args[0]} 실패 (종료 코드 {code}). 원본 보존. 상세 로그: {logPath}");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("반디집 응답 제한 시간(12시간) 초과. 원본 보존."); }
    }
    private sealed class BandizipExitException(string message) : IOException(message);
}
