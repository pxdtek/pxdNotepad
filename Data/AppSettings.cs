using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pxdNotepad.Data;

public static class AppPaths
{
    /// <summary>%APPDATA%\pxdNotepad, or PXDNOTEPAD_DATA (tests).</summary>
    public static string DataDir { get; set; } =
        Environment.GetEnvironmentVariable("PXDNOTEPAD_DATA") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxdNotepad");

    public static string Database => Path.Combine(DataDir, "notes.db");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 15;
    public bool LineNumbers { get; set; }
    public bool SpellCheck { get; set; } = true;
    public double SideWidth { get; set; } = 270;
    public bool SidebarOpen { get; set; } = true;
    /// <summary>Writing only: no sidebar, toolbar or status line, slim title bar.</summary>
    public bool Focus { get; set; }
    /// <summary>Writing mode stays on top of other windows (the pin in its title bar).</summary>
    public bool PinOnTop { get; set; }
    /// <summary>A thin outline around blurred text, so it's easy to spot in a long note.</summary>
    public bool BlurOutline { get; set; } = true;
    public string? LastNote { get; set; }
    public string? LastCategory { get; set; }
    public List<string> Collapsed { get; set; } = ["*trash"];
    public string SyncServer { get; set; } = "https://pxdtek.com";
    /// <summary>The sync key, encrypted for this Windows user (DPAPI). Empty: sync is off.</summary>
    public string SyncKey { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch
        {
            /* damaged: defaults */
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch
        {
            /* next change tries again */
        }
    }

    public byte[]? GetSyncKey()
    {
        if (string.IsNullOrEmpty(SyncKey)) return null;
        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(SyncKey), Encoding.UTF8.GetBytes("pxdNotepad"), DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    public void SetSyncKey(byte[]? key) =>
        SyncKey = key is null ? "" : Convert.ToBase64String(ProtectedData.Protect(key, Encoding.UTF8.GetBytes("pxdNotepad"), DataProtectionScope.CurrentUser));
}
