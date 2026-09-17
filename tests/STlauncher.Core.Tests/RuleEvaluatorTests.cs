using System.Collections.Generic;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

public class RuleEvaluatorTests
{
    private static RuleContext Context(string os, string arch = "x86_64", Dictionary<string, bool>? features = null)
        => new()
        {
            OsName = os,
            Arch = arch,
            Features = features ?? new Dictionary<string, bool>()
        };

    [Fact]
    public void NoRules_IsAllowed_OnEveryPlatform()
    {
        Assert.True(RuleEvaluator.IsAllowed(null, Context("windows")));
        Assert.True(RuleEvaluator.IsAllowed(new List<Rule>(), Context("osx")));
    }

    [Fact]
    public void AllowWindowsOnly_IsAllowed_OnWindows_ButNotElsewhere()
    {
        var rules = new List<Rule>
        {
            new() { Action = "allow", Os = new OsRule { Name = "windows" } }
        };

        Assert.True(RuleEvaluator.IsAllowed(rules, Context("windows")));
        Assert.False(RuleEvaluator.IsAllowed(rules, Context("linux")));
        Assert.False(RuleEvaluator.IsAllowed(rules, Context("osx")));
    }

    [Fact]
    public void LastMatchingRuleWins()
    {
        var rules = new List<Rule>
        {
            new() { Action = "allow" },
            new() { Action = "disallow", Os = new OsRule { Name = "osx" } }
        };

        Assert.True(RuleEvaluator.IsAllowed(rules, Context("windows")));
        Assert.False(RuleEvaluator.IsAllowed(rules, Context("osx")));
    }

    [Fact]
    public void FeatureRule_RequiresMatchingFeature()
    {
        var rules = new List<Rule>
        {
            new()
            {
                Action = "allow",
                Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true }
            }
        };

        Assert.True(RuleEvaluator.IsAllowed(rules, Context("windows", features: new Dictionary<string, bool>
        {
            ["has_custom_resolution"] = true
        })));

        Assert.False(RuleEvaluator.IsAllowed(rules, Context("windows")));
    }

    [Fact]
    public void ArchRule_FiltersByArchitecture()
    {
        var rules = new List<Rule>
        {
            new() { Action = "allow", Os = new OsRule { Name = "osx", Arch = "arm64" } }
        };

        Assert.True(RuleEvaluator.IsAllowed(rules, Context("osx", "arm64")));
        Assert.False(RuleEvaluator.IsAllowed(rules, Context("osx", "x86_64")));
    }
}