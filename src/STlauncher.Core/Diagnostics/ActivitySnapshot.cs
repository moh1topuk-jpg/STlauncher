using System;
using System.Collections.Generic;

namespace STlauncher.Core.Diagnostics;

/// <summary>
/// What the launcher was doing, readable from any thread. The interface writes here as
/// its section and busy flags change; the freeze watchdog reads it exactly when the
/// interface thread cannot be asked any more.
/// </summary>
public sealed class ActivitySnapshot
{
    private readonly object _gate = new();
    private readonly SortedDictionary<string, DateTimeOffset> _active = new(StringComparer.Ordinal);
    private string _section = string.Empty;
    private DateTimeOffset _sectionSince;

    public void SetSection(string section, DateTimeOffset now)
    {
        lock (_gate)
        {
            _section = section ?? string.Empty;
            _sectionSince = now;
        }
    }

    /// <summary>Raises or clears a "long operation in progress" flag.</summary>
    public void SetFlag(string name, bool on, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        lock (_gate)
        {
            if (!on)
            {
                _active.Remove(name);
            }
            else if (!_active.ContainsKey(name))
            {
                _active[name] = now;
            }
        }
    }

    /// <summary>One line per fact, with how long each has been true.</summary>
    public IReadOnlyList<string> Describe(DateTimeOffset now)
    {
        var lines = new List<string>();

        lock (_gate)
        {
            lines.Add(_section.Length == 0
                ? "section = unknown"
                : $"section = {_section} (for {Age(now - _sectionSince)})");

            if (_active.Count == 0)
            {
                lines.Add("busy flags = none");
            }

            foreach (var (name, since) in _active)
            {
                lines.Add($"{name} = true (for {Age(now - since)})");
            }
        }

        return lines;
    }

    private static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalMinutes >= 1
            ? $"{(int)age.TotalMinutes} min {age.Seconds} s"
            : $"{age.TotalSeconds:F0} s";
    }
}
