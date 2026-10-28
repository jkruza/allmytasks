using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AllMyTasks.Core.Autostart;

/// <summary>A value under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutostart : IAutostart
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "AllMyTasks";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public void Enable(AutostartLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, WindowsCommandLine.Build(launch), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
