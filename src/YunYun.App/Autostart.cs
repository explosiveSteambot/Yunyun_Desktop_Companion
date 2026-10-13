using Microsoft.Win32;

namespace YunYun.App;

/// <summary>
/// "Start with Windows": a value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run - the
/// standard per-user autostart list (also shown in Task Manager → Startup). No admin rights needed.
/// The registry is the source of truth, so turning it off in Task Manager is respected too.
/// </summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "YunYunDesktop";

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    static string Command => $"\"{ExePath}\"";

    /// <summary>Is there an autostart entry for the program (wherever it points)?</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// At startup: if the entry points to an exe that no longer exists (the program was moved),
    /// point it to this one. An entry for another existing copy is left alone.
    /// </summary>
    public static void RepairPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string s || s.Length == 0) return;
        string path = s.Trim().Trim('"');
        if (!File.Exists(path)) key.SetValue(ValueName, Command);
    }
}
