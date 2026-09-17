using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Metadata;

public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;

        foreach (var rule in rules)
        {
            if (Matches(rule, context))
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    private static bool Matches(Rule rule, RuleContext context)
    {
        if (rule.Os is not null)
        {
            if (rule.Os.Name is not null &&
                !string.Equals(rule.Os.Name, context.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (rule.Os.Arch is not null && context.Arch is not null &&
                !string.Equals(rule.Os.Arch, context.Arch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (rule.Os.Version is not null && context.OsVersion is not null &&
                !Regex.IsMatch(context.OsVersion, rule.Os.Version))
            {
                return false;
            }
        }

        if (rule.Features is not null)
        {
            foreach (var (key, expected) in rule.Features)
            {
                if (!context.Features.TryGetValue(key, out var actual) || actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }
}