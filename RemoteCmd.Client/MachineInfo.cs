using System.Runtime.InteropServices;
using RemoteCmd.Shared;

namespace RemoteCmd.Client;

/// <summary>
/// What the relay's dashboard shows about this machine beyond its alias: the real host name, the
/// system, the account the client runs as and its version. Sent with every poll, a few dozen bytes;
/// relays that do not know the fields ignore them.
/// </summary>
public static class MachineInfo
{
    private static readonly Lazy<string> Cached = new(() => string.Join('&',
        Pair("v", VersionInfo.Version),
        Pair("host", Environment.MachineName),
        Pair("user", Environment.UserName),
        Pair("os", $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}".Trim())));

    public static string Query() => Cached.Value;

    private static string Pair(string key, string value) => $"{key}={Uri.EscapeDataString(value)}";
}
