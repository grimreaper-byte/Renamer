using System.Text.RegularExpressions;

namespace Renamer.Core;

/// <summary>Regexes here can come from the user, so every one gets a timeout.</summary>
public static class RegexTools
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static Regex? TryCreate(string pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(pattern))
        {
            error = "Pattern is empty.";
            return null;
        }

        try
        {
            return new Regex(pattern, Options, Timeout);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static bool SafeIsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static string SafeReplace(Regex regex, string input, string replacement)
    {
        try
        {
            return regex.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    /// <summary>Compiles the enabled rules for one stage, leaving out any that do not compile.</summary>
    public static List<(Regex Regex, string Replace)> CompileRules(IEnumerable<RegexRule> rules, RuleStage stage)
    {
        var compiled = new List<(Regex, string)>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.Stage != stage)
            {
                continue;
            }

            var regex = TryCreate(rule.Find, out _);
            if (regex is not null)
            {
                compiled.Add((regex, rule.Replace ?? ""));
            }
        }

        return compiled;
    }
}
