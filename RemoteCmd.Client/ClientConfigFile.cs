using System.Text.Json;
using RemoteCmd.Shared;

namespace RemoteCmd.Client;

/// <summary>
/// The client's config file. The token lives here, readable only by the owner, instead of on a
/// command line every user on the machine can list.
/// </summary>
public static class ClientConfigFile
{
    public const string DefaultServiceName = "RemoteCmdClient";

    /// <summary>One file per service, so several clients can share a machine.</summary>
    public static string DefaultPath(string serviceName = DefaultServiceName)
        => Path.Combine(ConfigPaths.MachineDirectory(),
            serviceName == DefaultServiceName ? "client.json" : $"client-{serviceName}.json");

    public static ClientConfig? Load(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path)) : null;

    public static void Save(string path, ClientConfig config)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        ConfigPaths.RestrictToOwner(tmp);
        File.Move(tmp, path, overwrite: true);
    }
}
