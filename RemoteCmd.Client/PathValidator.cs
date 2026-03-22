/// <summary>
/// Validates file paths against allowed roots to prevent path traversal attacks.
/// </summary>
class PathValidator
{
    private readonly string[] _allowedRoots;

    public PathValidator(IEnumerable<string> allowedPaths)
    {
        _allowedRoots = allowedPaths
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();
    }

    /// <summary>
    /// Validates a path. Returns null on success, or an error message on rejection.
    /// </summary>
    public string? Validate(string path)
    {
        // Reject UNC paths (\\server\share)
        if (path.StartsWith(@"\\") || path.StartsWith("//"))
            return "UNC paths are not allowed.";

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return $"Invalid path: {ex.Message}";
        }

        foreach (var root in _allowedRoots)
        {
            if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return $"Path is outside allowed roots: {fullPath}";
    }

    /// <summary>
    /// Creates a PathValidator from policy config, falling back to the exe directory.
    /// </summary>
    public static PathValidator FromPolicy(CommandPolicyConfig config)
    {
        var roots = config.AllowedPaths.Count > 0
            ? config.AllowedPaths
            : [AppContext.BaseDirectory];

        return new PathValidator(roots);
    }
}
