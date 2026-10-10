using STlauncher.Core.Launch;
using Xunit;

namespace STlauncher.Core.Tests;

public class MemoryAdviceTests
{
    private const int Roomy = 65536;

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(1, 3072)]
    [InlineData(20, 3072)]
    [InlineData(21, 4096)]
    [InlineData(80, 4096)]
    [InlineData(81, 6144)]
    [InlineData(150, 6144)]
    [InlineData(151, 8192)]
    [InlineData(250, 8192)]
    [InlineData(251, 10240)]
    [InlineData(600, 10240)]
    public void Heap_follows_the_number_of_mods(int mods, int expected)
    {
        var advice = MemoryAdvice.Recommend(Roomy, null, mods, shaders: false);

        Assert.Equal(expected, advice.Mb);
        Assert.Equal(expected, advice.WantedMb);
        Assert.Equal(MemoryLimit.None, advice.LimitedBy);
    }

    [Fact]
    public void Shaders_add_a_gigabyte()
    {
        Assert.Equal(3072, MemoryAdvice.Recommend(Roomy, null, 0, shaders: true).Mb);
        Assert.Equal(7168, MemoryAdvice.Recommend(Roomy, null, 120, shaders: true).Mb);
    }

    [Fact]
    public void Never_past_the_ceiling()
    {
        // 10 GB for the mods and 1 GB for shaders is still under it; the ceiling is what
        // holds if the table ever grows.
        Assert.Equal(11264, MemoryAdvice.Recommend(Roomy, null, 400, shaders: true).Mb);
        Assert.Equal(MemoryAdvice.CeilingMb, MemoryAdvice.MachineLimitMb(Roomy));
        Assert.Equal(MemoryAdvice.CeilingMb, MemoryAdvice.MachineLimitMb(1024 * 1024));
    }

    [Theory]
    [InlineData(2048, 1024)]
    [InlineData(3072, 1024)]
    [InlineData(4096, 2048)]
    [InlineData(6144, 3072)]
    [InlineData(8192, 4096)]
    [InlineData(12288, 6144)]
    [InlineData(16384, 8192)]
    // Above 16 GB the half no longer applies: a quarter stays with the system.
    [InlineData(24576, 12288)]
    [InlineData(20480, 12288)]
    [InlineData(32768, 12288)]
    public void Machine_limit(int totalMb, int expected)
    {
        Assert.Equal(expected, MemoryAdvice.MachineLimitMb(totalMb));
    }

    [Theory]
    // What Windows reports on machines sold as 8, 16 and 32 GB.
    [InlineData(8031, 4096)]
    [InlineData(16303, 8192)]
    [InlineData(32691, 12288)]
    public void Machine_limit_reads_the_nominal_size(int reportedMb, int expected)
    {
        Assert.Equal(expected, MemoryAdvice.MachineLimitMb(reportedMb));
    }

    [Fact]
    public void A_big_pack_on_a_small_machine_gets_what_the_machine_can_give()
    {
        var advice = MemoryAdvice.Recommend(8192, null, 200, shaders: true);

        Assert.Equal(4096, advice.Mb);
        Assert.Equal(9216, advice.WantedMb);
        Assert.Equal(MemoryLimit.Machine, advice.LimitedBy);
    }

    [Fact]
    public void The_system_keeps_two_gigabytes_or_a_quarter()
    {
        foreach (var total in new[] { 4096, 6144, 8192, 12288, 16384, 24576, 32768, 65536 })
        {
            var mb = MemoryAdvice.Recommend(total, null, 1000, shaders: true).Mb;

            Assert.True(total - mb >= 2048, $"{total}: {mb}");
            Assert.True(total - mb >= total / 4, $"{total}: {mb}");
        }
    }

    [Fact]
    public void Floor_holds_on_a_machine_with_nothing_to_give()
    {
        Assert.Equal(MemoryAdvice.FloorMb, MemoryAdvice.Recommend(1024, null, 0, false).Mb);
        Assert.Equal(MemoryAdvice.FloorMb, MemoryAdvice.Recommend(0, null, 300, true).Mb);
        Assert.Equal(MemoryAdvice.FloorMb, MemoryAdvice.Recommend(16384, 300, 100, false).Mb);
    }

    [Fact]
    public void Free_memory_lowers_the_advice_when_it_is_known()
    {
        // 16 GB machine, 120 mods: 6 GB. Only 4.2 GB are free right now.
        var busy = MemoryAdvice.Recommend(16384, 4300, 120, false);

        Assert.Equal(3584, busy.Mb);
        Assert.Equal(MemoryLimit.FreeMemory, busy.LimitedBy);

        var idle = MemoryAdvice.Recommend(16384, 12000, 120, false);

        Assert.Equal(6144, idle.Mb);
        Assert.Equal(MemoryLimit.None, idle.LimitedBy);
    }

    [Fact]
    public void Unknown_free_memory_changes_nothing()
    {
        Assert.Equal(MemoryAdvice.Recommend(16384, null, 120, false), MemoryAdvice.Recommend(16384, 0, 120, false));
    }
}
