// pxd App Standard rule 6 (self-update), copied unchanged from github.com/pxdtek/pxdToolkit docs/standard/pxdStandard.cs.
// The rest of that file is Avalonia-only; pxd Notepad (WPF) has its own port of rules 1-3 in Standard/Chrome.cs.
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pxdStandard;

/// <summary>
/// Updates an installed app before it opens: call <see cref="BeforeStart"/> first thing in Main, before the
/// single-instance check, and exit when it returns true (the new version has been started in its place).
/// It asks GitHub for the app's latest release (a few seconds at most; skipped offline or when GitHub doesn't answer),
/// and when that is newer than this copy it downloads it, checks its SHA-256, swaps the app's files (a running file can
/// be renamed, not overwritten, so the old ones are set aside as *.pxdold and removed at the next start) and starts the
/// new copy with the same arguments. Developer builds (bin\ folders), --self-test runs and PXD_NO_UPDATE=1 are left alone.
/// </summary>
public static class pxdUpdate
{
    private const string Old = ".pxdold";
    private const string WaitEnv = "PXD_UPDATED_FROM";

    /// <summary>The release folder this copy came from, e.g. pxdtek/pxdWatermark and ^pxdWatermark-.*-win-x64\.zip$.</summary>
    public static bool BeforeStart(string repo, string assetPattern, string[] args, Version? current = null)
    {
        try
        {
            WaitForPrevious();
            var exe = Environment.ProcessPath;
            if (exe is null || !OperatingSystem.IsWindows())
                return false;
            var dir = Path.GetDirectoryName(exe)!;
            CleanUp(dir);
            // Already running (this start will just hand over to it): never swap files under a running copy.
            if (OtherCopyRunning(exe))
                return false;
            if (Environment.GetEnvironmentVariable("PXD_NO_UPDATE") == "1"
                || args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-update", StringComparison.OrdinalIgnoreCase))
                || dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
                return false;

            current ??= ThisVersion();
            var plan = FindUpdateAsync(repo, assetPattern, current).GetAwaiter().GetResult();
            if (plan is null)
                return false;
            return ApplyAsync(plan.Value.Zip, plan.Value.Sum, dir, exe, args).GetAwaiter().GetResult();
        }
        catch
        {
            return false;   // never stop the app from opening
        }
    }

    /// <summary>This copy's version: the entry assembly's informational version without the "+commit" part.</summary>
    public static Version ThisVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return ParseVersion(info) ?? asm?.GetName().Version ?? new Version(0, 0);
    }

    public static Version? ParseVersion(string? s)
    {
        var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
    }

    /// <summary>
    /// Where release info is asked for, in order: pxdtek.com (it fetches from GitHub on its side, so clients only ever talk
    /// to pxdtek.com and GitHub's limit is paid once there), then GitHub itself for networks that block pxdtek.com.
    /// PXD_UPDATE_API replaces both (tests).
    /// </summary>
    public static string[] ApiBases() =>
        Environment.GetEnvironmentVariable("PXD_UPDATE_API") is { Length: > 0 } a
            ? [a.TrimEnd('/')]
            : ["https://pxdtek.com/gh", "https://api.github.com"];

    /// <summary>pxdtek.com's copy of a GitHub release download → the same file on GitHub (the fallback).</summary>
    public static string? GitHubCopy(string url)
    {
        var m = Regex.Match(url, @"^https://pxdtek\.com/dl/([^/]+)/([^/]+)/([^/]+)$");
        return m.Success ? $"https://github.com/pxdtek/{m.Groups[1].Value}/releases/download/{m.Groups[2].Value}/{m.Groups[3].Value}" : null;
    }

    private static async Task<string> GetWithFallbackAsync(HttpClient http, string url)
    {
        try
        {
            return await http.GetStringAsync(url);
        }
        catch when (GitHubCopy(url) is not null)
        {
            return await http.GetStringAsync(GitHubCopy(url));
        }
    }

    /// <summary>The newer release's download (and checksum) URLs, or null when this copy is current or nothing could be asked.</summary>
    public static async Task<(string Zip, string? Sum)?> FindUpdateAsync(string repo, string assetPattern, Version current)
    {
        using var http = Client(TimeSpan.FromSeconds(4));
        JsonDocument? doc = null;
        foreach (var api in ApiBases())
        {
            try
            {
                var parsed = JsonDocument.Parse(await http.GetStringAsync($"{api}/repos/{repo}/releases/latest"));
                if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("tag_name", out _))
                {
                    doc = parsed;
                    break;
                }

                parsed.Dispose();
            }
            catch
            {
                /* blocked, offline or not JSON (a filter's block page): try the next */
            }
        }

        if (doc is null)
            return null;
        using var release = doc;
        var root = doc.RootElement;
        if (ParseVersion(root.GetProperty("tag_name").GetString()) is not { } latest || latest <= current)
            return null;
        string? zip = null, sum = null, zipName = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (zipName is null && Regex.IsMatch(name, assetPattern))
            {
                zipName = name;
                zip = asset.GetProperty("browser_download_url").GetString();
            }
        }

        if (zip is null)
            return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == zipName + ".sha256")
                sum = asset.GetProperty("browser_download_url").GetString();
        return (zip, sum);
    }

    /// <summary>Downloads, checks, swaps the files in <paramref name="dir"/> and starts the new <paramref name="exe"/>. False if anything failed (nothing is left half-done).</summary>
    public static async Task<bool> ApplyAsync(string zipUrl, string? sumUrl, string dir, string exe, string[] args, bool start = true)
    {
        var stage = Path.Combine(Path.GetTempPath(), "pxd-update-" + Guid.NewGuid().ToString("N"));
        var moved = new List<string>();
        var added = new List<string>();
        try
        {
            Directory.CreateDirectory(stage);
            var zip = Path.Combine(stage, "update.zip");
            using (var http = Client(TimeSpan.FromMinutes(5)))
            {
                var got = false;
                try
                {
                    await using (var src = await http.GetStreamAsync(zipUrl))
                    await using (var dst = File.Create(zip))
                        await src.CopyToAsync(dst);
                    got = IsZip(zip);   // a web filter's block page is not a zip
                }
                catch when (GitHubCopy(zipUrl) is not null)
                {
                    /* pxdtek.com blocked or down: GitHub below */
                }

                if (!got)
                {
                    // The same file straight from GitHub.
                    var alt = GitHubCopy(zipUrl) ?? throw new InvalidOperationException("download failed");
                    await using (var src = await http.GetStreamAsync(alt))
                    await using (var dst = File.Create(zip))
                        await src.CopyToAsync(dst);
                    if (!IsZip(zip))
                        return false;
                }

                if (sumUrl is not null)
                {
                    var expected = Regex.Match(await GetWithFallbackAsync(http, sumUrl), "[0-9a-fA-F]{64}").Value;
                    await using var fs = File.OpenRead(zip);
                    var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs));
                    if (expected.Length != 64 || !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))   // no hash (a block page) counts as a failure
                        return false;
                }
            }

            var unz = Path.Combine(stage, "x");
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, unz);
            var root = unz;
            if (Directory.GetFiles(unz).Length == 0 && Directory.GetDirectories(unz) is [var only])
                root = only;
            if (!File.Exists(Path.Combine(root, Path.GetFileName(exe))))
                return false;   // not this app's download

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(dir, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    File.Move(dest, dest + Old, overwrite: true);
                    moved.Add(dest);
                }
                else
                    added.Add(dest);
                File.Copy(file, dest);
            }

            if (start)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
                psi.Environment[WaitEnv] = Environment.ProcessId.ToString();
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }

            return true;
        }
        catch
        {
            // Put the old files back.
            foreach (var f in added)
                try { File.Delete(f); } catch { /* best effort */ }
            foreach (var f in moved)
                try { File.Move(f + Old, f, overwrite: true); } catch { /* best effort */ }
            return false;
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
        }
    }

    /// <summary>The file starts like a zip ("PK"), not like a web filter's HTML block page.</summary>
    public static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the files an update set aside (they are free once the old copy has closed).</summary>
    public static void CleanUp(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*" + Old, SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* still in use: next start */ }
        }
        catch
        {
            /* not readable */
        }
    }

    /// <summary>Another process started from the same exe file is running.</summary>
    public static bool OtherCopyRunning(string exe)
    {
        var found = false;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                if (found || p.Id == Environment.ProcessId)
                    continue;
                try
                {
                    found = string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    found = true;   // can't tell (elevated copy): assume it's ours and leave the files alone
                }
            }
        }

        return found;
    }

    /// <summary>A copy started by an update waits for the old one to close, so its single-instance check doesn't find it.</summary>
    private static void WaitForPrevious()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(WaitEnv), out var pid))
            return;
        Environment.SetEnvironmentVariable(WaitEnv, null);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(15000);
        }
        catch
        {
            /* already gone */
        }
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("pxd-update/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
