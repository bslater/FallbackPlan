using Bodu;
using System.Text;
using System.Text.RegularExpressions;

namespace FallbackPlan.Domain;

/// <summary>
/// One compiled <c>rules-v1</c> include/exclude rule (specification 06 §7.1;
/// ADR-0024): a glob rule, or a <c>re:</c>-prefixed rule in the pinned
/// portable regex subset. Matching is over the entry's <c>/</c>-separated,
/// NFC-normalised relative path within the backup-set root, always as a full
/// match.
/// </summary>
public sealed class PathRule
{
    private readonly Regex _matcher;

    private PathRule(string text, Regex matcher)
    {
        Text = text;
        _matcher = matcher;
    }

    /// <summary>The rule exactly as it appears in the policy manifest.</summary>
    public string Text { get; }

    /// <summary>
    /// Compiles one rule. Returns <see langword="false"/> with the defect
    /// named for any invalid rule — a writer MUST refuse to publish it
    /// (specification 06 §7.1).
    /// </summary>
    public static bool TryCreate(string rule, bool caseSensitive, out PathRule? compiled, out string? defect)
    {
        ThrowHelper.ThrowIfNull(rule);
        compiled = null;

        var pattern = rule.StartsWith("re:", StringComparison.Ordinal)
            ? ValidateRegexSubset(rule[3..], out defect)
            : GlobToRegex(rule, out defect);

        if (pattern is null)
        {
            return false;
        }

        // NonBacktracking gives linear-time matching and structurally rejects
        // everything outside the subset (backreferences, lookaround) that a
        // hand validator might one day miss.
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant
            | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);

        try
        {
            compiled = new PathRule(rule, new Regex($"^(?:{pattern})$", options));
        }
        catch (ArgumentException exception)
        {
            defect = $"the pattern does not compile: {exception.Message}";
            return false;
        }

        defect = null;
        return true;
    }

    /// <summary>Whether the rule matches <paramref name="path"/> in full.</summary>
    public bool Matches(string path)
    {
        ThrowHelper.ThrowIfNull(path);
        return _matcher.IsMatch(path);
    }

    private static string? GlobToRegex(string rule, out string? defect)
    {
        if (rule.Length == 0)
        {
            defect = "the empty rule is invalid";
            return null;
        }

        // A rule without '/' is shorthand for **/<rule> (06 §7.1).
        var components = rule.Contains('/', StringComparison.Ordinal)
            ? rule.Split('/')
            : ["**", rule];

        if (components.Contains(string.Empty))
        {
            defect = "a rule with an empty component (leading /, trailing /, or //) is invalid";
            return null;
        }

        var pattern = new StringBuilder();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            var last = index == components.Length - 1;

            if (component == "**")
            {
                // Trailing ** matches one or more further components (never
                // the prefix itself); elsewhere it matches zero or more
                // whole components including the joining slash.
                pattern.Append(last ? ".+" : "(?:[^/]+/)*");
                continue;
            }

            if (component.Contains("**", StringComparison.Ordinal))
            {
                defect = "** is valid only as a complete component";
                return null;
            }

            foreach (var character in component)
            {
                _ = character switch
                {
                    '*' => pattern.Append("[^/]*"),
                    '?' => pattern.Append("[^/]"),
                    _ => pattern.Append(Regex.Escape(character.ToString())),
                };
            }

            if (!last)
            {
                pattern.Append('/');
            }
        }

        defect = null;
        return pattern.ToString();
    }

    private static string? ValidateRegexSubset(string pattern, out string? defect)
    {
        if (pattern.Length == 0)
        {
            defect = "the empty rule is invalid";
            return null;
        }

        if (pattern.Split('/').Contains(string.Empty))
        {
            defect = "a rule with an empty component (leading /, trailing /, or //) is invalid";
            return null;
        }

        var inClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];

            if (character == '\\')
            {
                if (index + 1 >= pattern.Length)
                {
                    defect = "a trailing backslash is invalid";
                    return null;
                }

                if (char.IsLetterOrDigit(pattern[index + 1]))
                {
                    defect = "backslash-alphanumeric escapes (shorthand classes, backreferences) are outside the rules-v1 subset";
                    return null;
                }

                index++;
                continue;
            }

            if (inClass)
            {
                inClass = character != ']';
                continue;
            }

            switch (character)
            {
                case '[':
                    inClass = true;
                    break;

                case '^' or '$':
                    defect = "anchors are outside the rules-v1 subset — rules are implicitly anchored";
                    return null;

                case '(' when index + 1 < pattern.Length && pattern[index + 1] == '?':
                    defect = "(?...) constructs are outside the rules-v1 subset";
                    return null;

                case '{':
                    var quantifier = Regex.Match(pattern[index..], @"^\{[0-9]+(,[0-9]*)?\}");
                    if (!quantifier.Success)
                    {
                        defect = "an unescaped { must open a counted quantifier";
                        return null;
                    }

                    index += quantifier.Length - 1;
                    break;

                case '}':
                    defect = "an unescaped } outside a quantifier is invalid";
                    return null;
            }
        }

        if (inClass)
        {
            defect = "an unterminated character class is invalid";
            return null;
        }

        defect = null;
        return pattern;
    }
}

/// <summary>
/// A validated set of <c>rules-v1</c> include and exclude rules with the
/// specification 06 §7.1 evaluation: exclude wins and prunes subtrees via
/// ancestors; a path is captured when it is not excluded and the include
/// list is empty or reaches it or an ancestor.
/// </summary>
public sealed class PathRuleSet
{
    private readonly IReadOnlyList<PathRule> _includes;
    private readonly IReadOnlyList<PathRule> _excludes;

    private PathRuleSet(IReadOnlyList<PathRule> includes, IReadOnlyList<PathRule> excludes)
    {
        _includes = includes;
        _excludes = excludes;
    }

    /// <summary>
    /// Compiles a rule set, collecting one named defect per invalid rule.
    /// A writer MUST NOT publish a policy manifest whose rules fail here.
    /// </summary>
    public static bool TryCreate(
        IEnumerable<string> includeRules,
        IEnumerable<string> excludeRules,
        bool caseSensitive,
        out PathRuleSet? rules,
        out IReadOnlyList<string> defects)
    {
        ThrowHelper.ThrowIfNull(includeRules);
        ThrowHelper.ThrowIfNull(excludeRules);

        var found = new List<string>();
        var includes = Compile(includeRules, caseSensitive, "include", found);
        var excludes = Compile(excludeRules, caseSensitive, "exclude", found);

        defects = found;
        rules = found.Count == 0 ? new PathRuleSet(includes, excludes) : null;
        return rules is not null;
    }

    private static List<PathRule> Compile(IEnumerable<string> rules, bool caseSensitive, string kind, List<string> defects)
    {
        var compiled = new List<PathRule>();
        foreach (var rule in rules)
        {
            if (PathRule.TryCreate(rule, caseSensitive, out var one, out var defect))
            {
                compiled.Add(one!);
            }
            else
            {
                defects.Add($"{kind} rule '{rule}': {defect}");
            }
        }

        return compiled;
    }

    /// <summary>Whether the path or any ancestor matches an exclude rule.</summary>
    public bool IsExcluded(string path) => AnyPrefixMatches(_excludes, path);

    /// <summary>Whether the path is captured (06 §7.1 evaluation).</summary>
    public bool IsCaptured(string path) =>
        !IsExcluded(path) && (_includes.Count == 0 || AnyPrefixMatches(_includes, path));

    /// <summary>
    /// Whether a scanner may descend the directory: any non-excluded
    /// directory is descendable — descending never captures it
    /// (06 §7.1 rule 3).
    /// </summary>
    public bool MayDescend(string directoryPath) => !IsExcluded(directoryPath);

    private static bool AnyPrefixMatches(IReadOnlyList<PathRule> rules, string path)
    {
        ThrowHelper.ThrowIfNull(path);

        if (rules.Count == 0)
        {
            return false;
        }

        // Test the path and every ancestor prefix: a rule matching an
        // ancestor applies to the whole subtree.
        for (var end = path.Length; end > 0; end = path.LastIndexOf('/', end - 1))
        {
            var prefix = path[..end];
            foreach (var rule in rules)
            {
                if (rule.Matches(prefix))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
