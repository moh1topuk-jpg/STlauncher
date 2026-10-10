using System;

namespace STlauncher.Core.Launch;

public enum MemoryLimit
{
    /// <summary>The number is what the build asks for.</summary>
    None,

    /// <summary>The build would take more, but this computer should not give it.</summary>
    Machine,

    /// <summary>Other programs hold the memory right now.</summary>
    FreeMemory
}

/// <param name="Mb">The heap to recommend, in megabytes.</param>
/// <param name="WantedMb">What a build of this size takes on a machine with room to spare.</param>
/// <param name="LimitedBy">Why <paramref name="Mb"/> is lower than <paramref name="WantedMb"/>, if it is.</param>
public sealed record MemoryRecommendation(int Mb, int WantedMb, MemoryLimit LimitedBy);

/// <summary>
/// How much memory to give the game. A quarter of the RAM, which this replaces, gave a
/// plain game 8 GB on a big machine and a pack of two hundred mods 4 GB on an ordinary
/// one; what the game needs follows from what is in the build, and the machine only says
/// where to stop.
///
/// This is advice. It is shown beside the slider, used for a build that is being made,
/// and offered after a crash; it never changes the number a player has set.
/// </summary>
public static class MemoryAdvice
{
    /// <summary>Past this the garbage collector's pauses grow and nothing else does.</summary>
    public const int CeilingMb = 12288;

    /// <summary>Below this the game does not start at all.</summary>
    public const int FloorMb = 1024;

    public const int ShadersExtraMb = 1024;

    /// <summary>What Windows and everything else that is open keep for themselves, at least.</summary>
    private const int SystemReserveMb = 2048;

    /// <summary>Left free on top of the heap: Java takes more than its heap.</summary>
    private const int FreeMarginMb = 512;

    private const int StepMb = 512;

    /// <param name="totalMb">Physical memory of the machine.</param>
    /// <param name="freeMb">Memory nothing is using right now, when it is known.</param>
    /// <param name="enabledMods">Mods that are switched on in the build.</param>
    /// <param name="shaders">A shader pack is in use.</param>
    public static MemoryRecommendation Recommend(int totalMb, int? freeMb, int enabledMods, bool shaders)
    {
        var wanted = ForMods(enabledMods) + (shaders ? ShadersExtraMb : 0);
        var limit = MachineLimitMb(totalMb);

        var mb = Math.Min(wanted, limit);
        var limitedBy = wanted > limit ? MemoryLimit.Machine : MemoryLimit.None;

        if (freeMb is { } free && free > 0)
        {
            var room = Math.Max(FloorMb, (free - FreeMarginMb) / StepMb * StepMb);

            if (room < mb)
            {
                mb = room;
                limitedBy = MemoryLimit.FreeMemory;
            }
        }

        return new MemoryRecommendation(mb, wanted, limitedBy);
    }

    /// <summary>The heap a build with this many mods takes, before the machine has its say.</summary>
    public static int ForMods(int enabledMods) => enabledMods switch
    {
        <= 0 => 2048,
        <= 20 => 3072,
        <= 80 => 4096,
        <= 150 => 6144,
        <= 250 => 8192,
        _ => 10240
    };

    /// <summary>
    /// The most this machine should give the game: half of the RAM where there are 16 GB
    /// or less, never so much that under 2 GB or a quarter of the RAM is left for the
    /// system, and never past <see cref="CeilingMb"/>.
    /// </summary>
    public static int MachineLimitMb(int totalMb)
    {
        // A machine sold as 16 GB reports a little less; the rules are about the 16.
        var total = Math.Max(1024, (int)Math.Round(totalMb / 1024d, MidpointRounding.AwayFromZero) * 1024);

        var limit = CeilingMb;

        if (total <= 16384)
        {
            limit = Math.Min(limit, total / 2);
        }

        limit = Math.Min(limit, total - Math.Max(SystemReserveMb, total / 4));

        return Math.Max(FloorMb, limit / StepMb * StepMb);
    }
}
