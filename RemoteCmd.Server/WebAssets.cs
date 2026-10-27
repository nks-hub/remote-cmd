using System.Reflection;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The dashboard's pages, styles and scripts, compiled into the server from the Web folder so a
/// single-file relay needs nothing beside it. Being real files, the scripts are syntax-checked by
/// DashboardWebTests and CI; a syntax error in one stops every button on its page.
///
/// Every response carries a Content-Security-Policy that allows scripts and styles from this listener
/// only: the pages show text from machines someone else controls, and even a slip in the
/// text-node-only rendering could then not run anything.
/// </summary>
public static class WebAssets
{
    private const string Prefix = "RemoteCmd.Server.Web.";

    public const string Csp =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly Dictionary<string, (byte[] Bytes, string Type, string ETag)> Files = Load();

    /// <summary>Names of every bundled file, e.g. <c>app.js</c>.</summary>
    public static IEnumerable<string> Names => Files.Keys;

    /// <summary>The text of a bundled file, for tests.</summary>
    public static string Text(string name) => Encoding.UTF8.GetString(Files[name].Bytes);

    public static bool Exists(string name) => Files.ContainsKey(name);

    /// <summary>Serves a bundled file with caching that still picks up a new relay version at once.</summary>
    public static IResult Serve(HttpContext context, string name)
    {
        if (!Files.TryGetValue(name, out var file)) return Results.NotFound();
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = Csp;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-cache";
        headers.ETag = file.ETag;
        if (context.Request.Headers.IfNoneMatch == file.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(file.Bytes, file.Type);
    }

    private static Dictionary<string, (byte[], string, string)> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, (byte[], string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var name = resource[Prefix.Length..];
            var etag = $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..16]}\"";
            files[name] = (bytes, TypeOf(name), etag);
        }
        return files;
    }

    private static string TypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
