using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gonio.Launcher;
public record UpdateConfig(string Repository, string PublicKeyPem);
public record ReleaseManifest(string Version, string Url, string Sha256, long Size);
public record SignedManifest(string Payload, string Signature);
public record InstallState(string Current, string? Previous = null, string? Pending = null, string? Rejected = null);
public static class Updates
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const long MaxDownload = 250L * 1024 * 1024;
    public static string ValidVersion(string value) => Regex.IsMatch(value, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$") && Version.TryParse(value, out _) ? value : throw new InvalidDataException("Invalid release version");
    public static void ValidateRepository(string repository)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$") || repository.Contains("..")) throw new InvalidDataException("GitHub repository must be owner/name");
    }
    public static ReleaseManifest Verify(string envelope, UpdateConfig config)
    {
        ValidateRepository(config.Repository);
        var signed = JsonSerializer.Deserialize<SignedManifest>(envelope, Json) ?? throw new InvalidDataException("Missing manifest");
        var payload = Convert.FromBase64String(signed.Payload);
        using var rsa = RSA.Create(); rsa.ImportFromPem(config.PublicKeyPem);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(payload, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidDataException("更新ファイルの署名が一致しません。");
        var release = JsonSerializer.Deserialize<ReleaseManifest>(payload, Json) ?? throw new InvalidDataException("Missing release");
        ValidVersion(release.Version);
        var expected = $"https://github.com/{config.Repository}/releases/download/v{release.Version}/GonioWeb-update-{release.Version}.zip";
        if (release.Url != expected || !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$") || release.Size <= 0 || release.Size > MaxDownload) throw new InvalidDataException("Invalid release metadata");
        return release;
    }
    public static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(path);
    public static async Task Fetch(HttpClient http, string url, string target, long limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new IOException("HTTPS required");
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Download too large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920]; long size = 0; int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0) { size += n; if (size > limit) throw new IOException("Download too large"); await output.WriteAsync(buffer.AsMemory(0,n),ct); }
    }
    public static void ExtractVerified(string archive, string target, ReleaseManifest release)
    {
        using (var input = File.OpenRead(archive))
        {
            if (input.Length != release.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新ファイルが破損しています。");
        }
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 750L * 1024 * 1024) throw new InvalidDataException("Archive limits exceeded");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Split('/').Any(p => p == ".." || p == "." || p.EndsWith(' ') || p.EndsWith('.')) || !names.Add(name)) throw new InvalidDataException("Unsafe archive path");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Links are not allowed");
        }
        Directory.CreateDirectory(target);
        zip.ExtractToDirectory(target); // .NET also enforces destination containment.
        if (!File.Exists(Path.Combine(target, "GonioWeb.exe")) || !File.Exists(Path.Combine(target, "wwwroot", "apps", "web", "index.html"))) throw new InvalidDataException("Incomplete application");
        if (File.ReadAllText(Path.Combine(target, "version.txt")).Trim() != release.Version) throw new InvalidDataException("Version mismatch");
    }
    public static async Task CheckAndStage(string root, UpdateConfig config, Action<string> status, CancellationToken ct, HttpMessageHandler? transport = null)
    {
        if (string.IsNullOrWhiteSpace(config.Repository) || string.IsNullOrWhiteSpace(config.PublicKeyPem)) { status("自動更新は未設定です。GitHub配信先と公開鍵の設定が必要です。"); return; }
        ValidateRepository(config.Repository);
        using var http = transport == null ? new HttpClient() : new HttpClient(transport);
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GonioWeb-Updater/0.4.0");
        var scratch = Path.Combine(root,"staging",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            status("GitHubで更新を確認しています。");
            var manifestFile = Path.Combine(scratch,"manifest.json");
            await Fetch(http,$"https://github.com/{config.Repository}/releases/latest/download/update-manifest.json",manifestFile,65536,ct);
            var release = Verify(await File.ReadAllTextAsync(manifestFile,ct),config);
            var state = Read<InstallState>(Path.Combine(root,"state.json"));
            if (Version.Parse(release.Version) <= Version.Parse(ValidVersion(state.Current)) || state.Rejected == release.Version) { status(state.Rejected==release.Version ? "起動失敗した更新を保留しています。現在の版を継続します。" : "最新バージョンです（"+state.Current+"）。"); return; }
            if (state.Pending == release.Version) { status("更新 "+release.Version+" を準備済みです。次回起動時に適用します。"); return; }
            status("更新 "+release.Version+" をダウンロードしています。計測は継続できます。");
            var archive=Path.Combine(scratch,"update.zip"); await Fetch(http,release.Url,archive,MaxDownload,ct);
            var extracted=Path.Combine(scratch,"app"); ExtractVerified(archive,extracted,release);
            var destination=Path.Combine(root,"versions",release.Version);
            // Preserve an orphan from a crash, then stage the newly verified download.
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination)) Directory.Move(destination,destination+".orphan-"+Guid.NewGuid().ToString("N"));
            Directory.Move(extracted,destination);
            AtomicJson(Path.Combine(root,"state.json"),state with {Pending=release.Version});
            status("更新 "+release.Version+" の準備ができました。アプリを終了し次回起動すると適用します。");
        }
        finally { try {Directory.Delete(scratch,true);} catch {} }
    }
}
