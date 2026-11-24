using System;
using Microsoft.Win32;

namespace WheelConvert.Core;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WheelConvert";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is not null;
    }

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enable) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, throwOnMissingValue: false);
    }
}
