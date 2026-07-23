using System.Reflection;

namespace RemoteCmd.Shared;

/// <summary>
/// Version of the running executable, stamped by the build via <c>-p:Version</c> in CI
/// (see release.yml). A plain local build with no version reports "dev".
/// Reads the entry assembly, so the same helper reports the client or server version.
/// </summary>
public static class VersionInfo
{
    public static string Version
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info))
            {
                var plus = info.IndexOf('+'); // strip the +<git sha> source-revision suffix
                return plus > 0 ? info[..plus] : info;
            }
            var asm = Assembly.GetEntryAssembly()?.GetName().Version;
            return asm is null || asm.ToString() == "1.0.0.0" ? "dev" : asm.ToString(3);
        }
    }
}
