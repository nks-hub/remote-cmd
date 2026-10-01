namespace RemoteCmd.Tests;

/// <summary>
/// Points the relay at a settings file of its own. Without it every test relay would read and write
/// the machine-wide one — and keep each other's pairings and credentials.
/// </summary>
public static class TestConfig
{
    public static string Fresh()
    {
        var path = Path.Combine(Path.GetTempPath(), "remotecmd-tests", Guid.NewGuid().ToString("N"), "server.json");
        Environment.SetEnvironmentVariable("REMOTECMD_CONFIG", path);
        // Tests must not depend on reaching GitHub.
        Environment.SetEnvironmentVariable("REMOTECMD_NO_UPDATE_CHECK", "1");
        return path;
    }
}
