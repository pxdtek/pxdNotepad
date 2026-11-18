using System.Diagnostics;

namespace pxdStandard;

/// <summary>
/// pxd App Standard 6 (update before anything opens), without making the start wait for the web: the check runs
/// while the window is being built, and the window shows only once it's answered (or after a few seconds, when the
/// next start checks again). If there's an update it's applied before anything shows, as with pxdUpdate.BeforeStart,
/// which this follows step by step using pxdUpdate's own parts.
/// </summary>
public static class UpdateAtStart
{
    public static Task<(string Zip, string? Sum)?>? Begin(string repo, string assetPattern, string[] args)
    {
        try
        {
            // A copy started by an update waits for the old one to close (before its single-instance check).
            if (int.TryParse(Environment.GetEnvironmentVariable("PXD_UPDATED_FROM"), out var pid))
            {
                Environment.SetEnvironmentVariable("PXD_UPDATED_FROM", null);
                try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); } catch { /* already gone */ }
            }
            var exe = Environment.ProcessPath;
            if (exe is null || !OperatingSystem.IsWindows()) return null;
            var dir = Path.GetDirectoryName(exe)!;
            if (Environment.GetEnvironmentVariable("PXD_NO_UPDATE") == "1"
                || args.Any(a => a.Equals("--no-update", StringComparison.OrdinalIgnoreCase))
                || dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
                return null;
            return Task.Run(async () =>
            {
                pxdUpdate.CleanUp(dir);
                // Already running (this start will just hand over to it): never swap files under a running copy.
                if (pxdUpdate.OtherCopyRunning(exe)) return ((string, string?)?)null;
                return await pxdUpdate.FindUpdateAsync(repo, assetPattern, pxdUpdate.ThisVersion()).ConfigureAwait(false);
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Before the window shows: true when an update was installed and started (this copy should just end).</summary>
    public static bool ApplyIfFound(Task<(string Zip, string? Sum)?>? check, string[] args)
    {
        if (check is null) return false;
        try
        {
            if (!check.Wait(TimeSpan.FromSeconds(5)) || check.Result is not { } plan) return false;
            var exe = Environment.ProcessPath!;
            return Task.Run(() => pxdUpdate.ApplyAsync(plan.Zip, plan.Sum, Path.GetDirectoryName(exe)!, exe, args)).GetAwaiter().GetResult();
        }
        catch
        {
            return false;   // never stop the app from opening
        }
    }
}
