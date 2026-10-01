using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteCmd.Shared;

/// <summary>Where configuration lives, and keeping files that hold secrets private.</summary>
public static class ConfigPaths
{
    /// <summary>
    /// The machine-wide directory when this process may write there (a service, an admin, root),
    /// otherwise the user's own — so a plain user can still pair and run in the foreground.
    /// </summary>
    public static string MachineDirectory()
    {
        var machine = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteCmd")
            : "/etc/remotecmd";
        if (CanWrite(machine)) return machine;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCmd")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "remotecmd");
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes a file readable only by its owner (Unix 0600) or, on Windows, by SYSTEM, the
    /// Administrators group and the current user. ProgramData is readable by every user by default,
    /// and these files hold tokens.
    /// </summary>
    public static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     WindowsIdentity.GetCurrent().User!,
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        new FileInfo(path).SetAccessControl(security);
    }
}
