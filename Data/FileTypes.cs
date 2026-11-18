using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace pxdNotepad.Data;

/// <summary>
/// Offers pxd Notepad for text files, for this Windows user only (HKEY_CURRENT_USER: no admin). It shows up in
/// "Open with" and in Settings → Apps → Default apps; Windows only lets the user make it the default (that choice
/// is theirs, by design), so after registering, the Default apps page is opened for them to pick it.
/// </summary>
public static class FileTypes
{
    public const string ProgId = "pxdNotepad.txt";
    public const string AppName = "pxd Notepad";
    public static readonly string[] Extensions = [".txt", ".log", ".md"];

    /// <summary>Where it all goes, under HKEY_CURRENT_USER ("Software"; the self-test uses its own key).</summary>
    public static string Root { get; set; } = "Software";

    private static string Classes => Root + @"\Classes";
    private static string Capabilities => Root + @"\pxd\pxdNotepad\Capabilities";

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ProgId}\shell\open\command");
        return k?.GetValue(null) is string;
    }

    public static string? RegisteredCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ProgId}\shell\open\command");
        return k?.GetValue(null) as string;
    }

    private static string Command(string exe) => $"\"{exe}\" \"%1\"";

    public static void Register(string exe)
    {
        var cu = Registry.CurrentUser;
        using (var p = cu.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            p.SetValue(null, "Text document (pxd Notepad)");
            p.SetValue("FriendlyTypeName", "Text document (pxd Notepad)");
        }
        using (var i = cu.CreateSubKey($@"{Classes}\{ProgId}\DefaultIcon")) i.SetValue(null, $"\"{exe}\",0");
        using (var c = cu.CreateSubKey($@"{Classes}\{ProgId}\shell\open\command")) c.SetValue(null, Command(exe));

        using (var a = cu.CreateSubKey($@"{Classes}\Applications\pxdNotepad.exe"))
            a.SetValue("FriendlyAppName", AppName);
        using (var c = cu.CreateSubKey($@"{Classes}\Applications\pxdNotepad.exe\shell\open\command")) c.SetValue(null, Command(exe));
        using (var st = cu.CreateSubKey($@"{Classes}\Applications\pxdNotepad.exe\SupportedTypes"))
            foreach (var ext in Extensions) st.SetValue(ext, "");

        foreach (var ext in Extensions)
            using (var o = cu.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids"))
                o.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

        using (var cap = cu.CreateSubKey(Capabilities))
        {
            cap.SetValue("ApplicationName", AppName);
            cap.SetValue("ApplicationDescription", "Fast notes with instant search; also edits plain text files.");
            cap.SetValue("ApplicationIcon", $"\"{exe}\",0");
        }
        using (var fa = cu.CreateSubKey(Capabilities + @"\FileAssociations"))
            foreach (var ext in Extensions) fa.SetValue(ext, ProgId);
        using (var ra = cu.CreateSubKey(Root + @"\RegisteredApplications"))
            ra.SetValue(AppName, Capabilities);
        Changed();
    }

    public static void Unregister()
    {
        var cu = Registry.CurrentUser;
        try { cu.DeleteSubKeyTree($@"{Classes}\{ProgId}", false); } catch { /* gone */ }
        try { cu.DeleteSubKeyTree($@"{Classes}\Applications\pxdNotepad.exe", false); } catch { /* gone */ }
        foreach (var ext in Extensions)
            using (var o = cu.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", true))
                try { o?.DeleteValue(ProgId, false); } catch { /* gone */ }
        try { cu.DeleteSubKeyTree(Root + @"\pxd\pxdNotepad", false); } catch { /* gone */ }
        using (var ra = cu.OpenSubKey(Root + @"\RegisteredApplications", true))
            try { ra?.DeleteValue(AppName, false); } catch { /* gone */ }
        Changed();
    }

    /// <summary>If pxd Notepad moved (a reinstall somewhere else), point the registration at where it is now.</summary>
    public static void RefreshIfRegistered(string exe)
    {
        try
        {
            if (RegisteredCommand() is { } cmd && cmd != Command(exe)) Register(exe);
        }
        catch
        {
            /* not allowed: leave it */
        }
    }

    /// <summary>Settings → Apps → Default apps, at pxd Notepad's page where Windows supports that.</summary>
    public static void OpenDefaultApps()
    {
        foreach (var uri in new[] { "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(AppName), "ms-settings:defaultapps" })
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
                return;
            }
            catch
            {
                /* try the plain page */
            }
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void Changed()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { /* Explorer catches up later */ }
    }
}
