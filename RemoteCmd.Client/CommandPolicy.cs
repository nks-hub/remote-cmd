using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Policy mode controlling how commands are validated.
/// </summary>
enum PolicyMode
{
    Allowlist,
    Denylist,
    Unrestricted
}

/// <summary>
/// Serializable configuration loaded from commandpolicy.json.
/// </summary>
class CommandPolicyConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "denylist";

    [JsonPropertyName("patterns")]
    public List<string> Patterns { get; set; } = [];

    [JsonPropertyName("allowedPaths")]
    public List<string> AllowedPaths { get; set; } = [];

    [JsonPropertyName("maxCommandLength")]
    public int MaxCommandLength { get; set; } = 4096;
}

/// <summary>
/// Validates commands against a configurable allowlist or denylist policy.
/// </summary>
class CommandValidator
{
    private static readonly string[] DefaultDenyPatterns =
    [
        @"Invoke-WebRequest",
        @"Invoke-RestMethod",
        @"Start-Process",
        @"-EncodedCommand",
        @"net\s+user",
        @"Add-LocalGroupMember",
        @"Set-ExecutionPolicy"
    ];

    private readonly PolicyMode _mode;
    private readonly Regex[] _patterns;
    private readonly int _maxLength;

    public CommandPolicyConfig Config { get; }

    public CommandValidator(CommandPolicyConfig config)
    {
        Config = config;

        _mode = config.Mode.ToLowerInvariant() switch
        {
            "allowlist" => PolicyMode.Allowlist,
            "unrestricted" => PolicyMode.Unrestricted,
            _ => PolicyMode.Denylist
        };

        _maxLength = config.MaxCommandLength > 0 ? config.MaxCommandLength : 4096;

        var rawPatterns = config.Patterns.Count > 0
            ? config.Patterns
            : (_mode == PolicyMode.Denylist ? [.. DefaultDenyPatterns] : new List<string>());

        _patterns = rawPatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();
    }

    /// <summary>
    /// Validates a command string. Returns null on success, or an error message on rejection.
    /// </summary>
    public string? Validate(string command)
    {
        if (command.Length > _maxLength)
            return $"Command exceeds maximum length of {_maxLength} characters.";

        if (_mode == PolicyMode.Unrestricted)
            return null;

        if (_mode == PolicyMode.Allowlist)
        {
            var matched = _patterns.Any(p => p.IsMatch(command));
            return matched ? null : "Command does not match any allowlist pattern.";
        }

        // Denylist
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(command))
                return $"Command blocked by denylist policy (pattern: {pattern}).";
        }

        return null;
    }

    /// <summary>
    /// Loads policy from commandpolicy.json next to the exe, or returns default denylist config.
    /// </summary>
    public static CommandValidator Load()
    {
        var exeDir = AppContext.BaseDirectory;
        var policyPath = Path.Combine(exeDir, "commandpolicy.json");

        if (File.Exists(policyPath))
        {
            try
            {
                var json = File.ReadAllText(policyPath);
                var config = JsonSerializer.Deserialize<CommandPolicyConfig>(json)
                             ?? new CommandPolicyConfig();
                return new CommandValidator(config);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POLICY] Warning: failed to load commandpolicy.json: {ex.Message}. Using defaults.");
            }
        }

        return new CommandValidator(new CommandPolicyConfig());
    }
}
