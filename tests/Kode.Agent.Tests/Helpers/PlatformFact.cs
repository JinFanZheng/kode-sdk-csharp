using System.Runtime.InteropServices;
using Xunit;

namespace Kode.Agent.Tests.Helpers;

/// <summary>
/// Conditional fact attribute that only runs on Unix-like platforms (Linux, macOS)
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test only runs on Unix-like platforms (Linux, macOS)";
        }
    }
}

/// <summary>
/// Conditional fact attribute that only runs on Windows
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test only runs on Windows";
        }
    }
}

/// <summary>
/// Platform-specific command helpers for cross-platform testing
/// </summary>
public static class PlatformCommands
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    
    /// <summary>
    /// Returns platform-specific command to list directory contents
    /// </summary>
    public static string ListDirectory => IsWindows ? "dir" : "ls -la";
    
    /// <summary>
    /// Returns platform-specific command to echo a variable
    /// </summary>
    public static string EchoVariable(string varName)
    {
        return IsWindows 
            ? $"echo %{varName}%" 
            : $"echo ${varName}";
    }
    
    /// <summary>
    /// Returns platform-specific command to echo text
    /// </summary>
    public static string Echo(string text)
    {
        return $"echo {text}";
    }
    
    /// <summary>
    /// Returns platform-specific shell executable
    /// </summary>
    public static string Shell => IsWindows ? "cmd.exe" : "/bin/sh";
    
    /// <summary>
    /// Returns platform-specific shell arguments prefix
    /// </summary>
    public static string ShellArgs(string command)
    {
        return IsWindows 
            ? $"/c {command}" 
            : $"-c \"{command}\"";
    }
}
