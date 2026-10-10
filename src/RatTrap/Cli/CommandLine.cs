namespace RatTrap.Cli;

/// <summary>
/// Minimal, allocation-light command line parser.
/// </summary>
/// <remarks>
/// Hand-written rather than pulled from a library, because this project has no package
/// references (see nuget.config) and because <c>System.CommandLine</c> would add a
/// dependency for what is a dozen options.
/// </remarks>
internal sealed class CommandLine
{
    /// <summary>
    /// Parsed <c>--name value</c> / <c>--name=value</c> / <c>-n</c> options, without the
    /// leading dashes. Case-insensitive.
    /// </summary>
    /// <remarks>
    /// This is a settable property rather than a field with an initialiser because
    /// <see cref="Parse"/> builds the dictionary in a local and must hand it over. An
    /// initialiser here would be silently discarded, which is exactly the bug that once
    /// made every option on this program's command line a no-op.
    /// </remarks>
    public Dictionary<string, string> Options { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = string.Empty;

    public IReadOnlyList<string> Positional { get; private init; } = [];

    private IReadOnlyDictionary<string, string> OptionsView => Options;

    public static CommandLine Parse(string[] args)
    {
        string command = string.Empty;
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string name = arg[2..];
                string value = "true";

                int equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }

                options[name] = value;
                continue;
            }

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                options[arg[1..]] = arg.Length > 2 ? arg[2..] : "true";
                continue;
            }

            if (command.Length == 0)
            {
                command = arg;
            }
            else
            {
                positional.Add(arg);
            }
        }

        return new CommandLine { Command = command, Positional = positional, Options = options };
    }

    public bool Has(string name) => OptionsView.ContainsKey(name);

    public string? Get(string name) => OptionsView.TryGetValue(name, out string? value) ? value : null;

    public string Get(string name, string fallback) => OptionsView.TryGetValue(name, out string? value) ? value : fallback;

    public int GetInt(string name, int fallback)
        => OptionsView.TryGetValue(name, out string? value) && int.TryParse(value, out int parsed) ? parsed : fallback;

    public double GetDouble(string name, double fallback)
        => OptionsView.TryGetValue(name, out string? value) && double.TryParse(value, out double parsed) ? parsed : fallback;

    public bool GetBool(string name, bool fallback = false)
        => OptionsView.TryGetValue(name, out string? value)
            ? value is "true" or "1" or "yes" or "on"
            : fallback;

    /// <summary>Renders the parsed options for logging, so a mistyped flag is visible.</summary>
    public string DescribeOptions()
        => OptionsView.Count == 0
            ? "(none)"
            : string.Join(", ", OptionsView.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                                            .Select(p => $"{p.Key}={p.Value}"));
}
