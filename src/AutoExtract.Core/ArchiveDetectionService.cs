using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoExtract.Core;

public sealed class ArchiveDetectionService
{
    private static readonly HashSet<string> Extensions = new(".zip .zipx .7z .rar .tar .tgz .tbz .tbz2 .txz .gz .gzip .bz .bz2 .xz .z .zst .zstd .lz .lzma .lz4 .cab .arj .lzh .lha .alz .egg .ace .bh .pea .zpaq .wim .xpi .asar .uue .uu .deb".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static bool Match(string s, string p) => Regex.IsMatch(s, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public bool IsArchive(string path)
    {
        var n = Path.GetFileName(path);
        if (Match(n, @"\.(part\d+\.rar|vol\d+\.egg|7z\.\d+|zip\.\d+|r\d\d|z\d\d|a\d\d)$")) return Match(n, @"\.(part0*1\.rar|vol0*1\.egg|7z\.0*1|zip\.0*1)$");
        return Extensions.Contains(Path.GetExtension(n));
    }
    public string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        var stem = Regex.Replace(name, @"\.(tar\.(gz|bz2|xz|zst|lz4)|part0*1\.rar|vol0*1\.egg|7z\.0*1|zip\.0*1)$", "", RegexOptions.IgnoreCase);
        if (stem == name) stem = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(stem) ? "Archive" : stem;
    }
    public string[] Family(string path)
    {
        var stem = Regex.Escape(BaseName(path));
        // Match the exact stem, never unrelated similarly prefixed archives.
        var name = Path.GetFileName(path);
        var suffix = Match(name, @"\.part0*1\.rar$") ? @"part\d+\.rar" :
            Match(name, @"\.vol0*1\.egg$") ? @"vol\d+\.egg" :
            Match(name, @"\.7z\.0*1$") ? @"7z\.\d+" : Match(name, @"\.zip\.0*1$") ? @"zip\.\d+" :
            Match(name, @"\.rar$") ? @"r\d\d" : Match(name, @"\.zip$") ? @"z\d\d" : Match(name, @"\.alz$") ? @"a\d\d" : @"(?!)";
        var pattern = @"^" + stem + @"\." + suffix + "$";
        return Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Where(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) || Match(Path.GetFileName(p), pattern)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public bool HasTemporaryCompanion(IEnumerable<string> files) => files.Any(p => new[] { ".crdownload", ".part", ".tmp", ".download" }.Any(ext => File.Exists(p + ext) || Directory.Exists(p + ext)));
    public static void RejectReparseAncestors(string path)
    {
        for (var p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("심볼릭 링크/재분석 경로는 자동 처리하지 않습니다: " + p);
    }
    public static async Task<string> SignatureAsync(IEnumerable<SourceLease> leases, CancellationToken token)
    {
        var parts = new List<string>();
        foreach (var l in leases)
        {
            l.Stream.Position = 0;
            var hash = await SHA256.HashDataAsync(l.Stream, token);
            parts.Add(Path.GetFileName(l.Path) + ":" + l.Stream.Length + ":" + Convert.ToHexString(hash));
            l.Stream.Position = 0;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))));
    }
}
