namespace STlauncher.Core.Emotes;

/// <summary>What an emote can move.</summary>
public enum EmotePart
{
    /// <summary>
    /// The whole figure: what turns over in a flip and rises in a jump. Files older than
    /// format version 3 call this "torso"; the reader sorts that out.
    /// </summary>
    Body,
    Head,

    /// <summary>The chest alone, turning about the neck.</summary>
    Torso,
    RightArm,
    LeftArm,
    RightLeg,
    LeftLeg
}

public enum EmoteAxis
{
    X,
    Y,
    Z,
    Pitch,
    Yaw,
    Roll
}

/// <summary>One value at one tick, and how to leave it for the next.</summary>
public readonly record struct EmoteKeyframe(int Tick, float Value, EmoteEasing Easing);

/// <summary>
/// Where one part is at an instant, as a difference from standing still: an offset of
/// its joint and a turn in radians, both zero for a part the emote leaves alone.
/// </summary>
/// <remarks>
/// The numbers are the game's own. For a limb, the head and the chest the offset is in
/// model pixels with y pointing down and the face looking along -z, and the turn is
/// applied pitch (about x) first, then yaw, then roll. For <see cref="EmotePart.Body"/>
/// the offset is in blocks with y pointing up.
/// </remarks>
public readonly record struct EmotePartState(float X, float Y, float Z, float Pitch, float Yaw, float Roll)
{
    public static EmotePartState Lerp(EmotePartState a, EmotePartState b, float t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t,
        a.Pitch + (b.Pitch - a.Pitch) * t,
        a.Yaw + (b.Yaw - a.Yaw) * t,
        a.Roll + (b.Roll - a.Roll) * t);
}

/// <summary>The whole figure at one instant of an emote.</summary>
public readonly record struct EmoteFrame(
    EmotePartState Body,
    EmotePartState Head,
    EmotePartState Torso,
    EmotePartState RightArm,
    EmotePartState LeftArm,
    EmotePartState RightLeg,
    EmotePartState LeftLeg)
{
    public static EmoteFrame Lerp(EmoteFrame a, EmoteFrame b, float t) => new(
        EmotePartState.Lerp(a.Body, b.Body, t),
        EmotePartState.Lerp(a.Head, b.Head, t),
        EmotePartState.Lerp(a.Torso, b.Torso, t),
        EmotePartState.Lerp(a.RightArm, b.RightArm, t),
        EmotePartState.Lerp(a.LeftArm, b.LeftArm, t),
        EmotePartState.Lerp(a.RightLeg, b.RightLeg, t),
        EmotePartState.Lerp(a.LeftLeg, b.LeftLeg, t));
}

/// <summary>
/// An Emotecraft emote, read into keyframes: for each part and each axis, what value it
/// has at which tick. Twenty ticks make a second.
/// </summary>
/// <remarks>
/// Sampling follows the mod's own player (KeyframeAnimationPlayer in playerAnimator), so
/// an emote here moves the way it does in the game: a part eases in from standing still
/// before its first keyframe, holds its last value to the end tick and eases back by
/// the stop tick, and a looping emote turns from the end tick back to the return tick.
/// Elbows and knees are not kept: nothing that draws from this model has them.
/// </remarks>
public sealed class Emote
{
    public const double TicksPerSecond = 20;

    private const int PartCount = 7;
    private const int AxisCount = 6;

    /// <summary>
    /// Where each joint sits in the game's model when nothing moves it. Position
    /// keyframes are absolute; what a frame reports is the distance from here.
    /// </summary>
    private static readonly float[] RestPosition =
    {
        0, 0, 0,        // body
        0, 0, 0,        // head
        0, 0, 0,        // torso
        -5, 2, 0,       // right arm
        5, 2, 0,        // left arm
        -1.9f, 12, 0.1f, // right leg
        1.9f, 12, 0.1f   // left leg
    };

    private readonly EmoteKeyframe[]?[] _tracks;

    internal Emote(
        string name, string author, string description,
        int beginTick, int endTick, int stopTick,
        bool isLoop, int returnTick, bool easeBeforeKeyframe,
        EmoteKeyframe[]?[] tracks)
    {
        Name = name;
        Author = author;
        Description = description;
        BeginTick = beginTick;
        EndTick = endTick;
        StopTick = stopTick;
        IsLoop = isLoop;
        ReturnTick = returnTick;
        EaseBeforeKeyframe = easeBeforeKeyframe;
        _tracks = tracks;
    }

    public string Name { get; }

    public string Author { get; }

    public string Description { get; }

    /// <summary>Until this tick the figure is still easing out of whatever it was doing.</summary>
    public int BeginTick { get; }

    /// <summary>The last tick of the movement itself; a looping emote turns back here.</summary>
    public int EndTick { get; }

    /// <summary>When an emote that does not loop is over, having eased back to standing.</summary>
    public int StopTick { get; }

    public bool IsLoop { get; }

    /// <summary>Where a looping emote resumes after <see cref="EndTick"/>.</summary>
    public int ReturnTick { get; }

    /// <summary>True when a stretch between two keyframes takes its easing from the later one.</summary>
    public bool EaseBeforeKeyframe { get; }

    /// <summary>How long one pass takes: to the stop for a plain emote, to the first turn-back for a loop.</summary>
    public double Seconds => (IsLoop ? EndTick + 1 : StopTick) / TicksPerSecond;

    /// <summary>How long each further pass of a looping emote takes.</summary>
    public double LoopSeconds => (EndTick + 1 - ReturnTick) / TicksPerSecond;

    /// <summary>The keyframes of one axis of one part, in tick order; empty when the emote leaves it alone.</summary>
    public IReadOnlyList<EmoteKeyframe> Keyframes(EmotePart part, EmoteAxis axis)
        => _tracks[(int)part * AxisCount + (int)axis] ?? (IReadOnlyList<EmoteKeyframe>)Array.Empty<EmoteKeyframe>();

    /// <summary>True when the emote moves this part at all.</summary>
    public bool Moves(EmotePart part)
    {
        for (var axis = 0; axis < AxisCount; axis++)
        {
            if (_tracks[(int)part * AxisCount + axis] is { Length: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The figure at a moment counted in ticks from the start, fractions included. Time
    /// may run on without end: a looping emote keeps turning back, a plain one stands
    /// still once it has stopped.
    /// </summary>
    public EmoteFrame Sample(double tick)
    {
        if (double.IsNaN(tick) || tick < 0)
        {
            tick = 0;
        }

        if (!IsLoop)
        {
            return tick >= StopTick ? default : SampleAt(tick, looped: false);
        }

        var looped = false;
        var cycle = EndTick + 1 - ReturnTick;

        if (tick >= EndTick + 1)
        {
            looped = true;
            tick = ReturnTick + (tick - ReturnTick) % cycle;
        }

        if (tick <= EndTick)
        {
            return SampleAt(tick, looped);
        }

        // The game shows the end tick, then the return tick, with nothing between. Drawn
        // at thirty frames a second that one tick is two frames, so they are spent going
        // from the one pose to the other instead of on a visible snap.
        return EmoteFrame.Lerp(SampleAt(EndTick, looped), SampleAt(ReturnTick, looped: true), (float)(tick - EndTick));
    }

    private EmoteFrame SampleAt(double tick, bool looped) => new(
        Part(EmotePart.Body, tick, looped),
        Part(EmotePart.Head, tick, looped),
        Part(EmotePart.Torso, tick, looped),
        Part(EmotePart.RightArm, tick, looped),
        Part(EmotePart.LeftArm, tick, looped),
        Part(EmotePart.RightLeg, tick, looped),
        Part(EmotePart.LeftLeg, tick, looped));

    private EmotePartState Part(EmotePart part, double tick, bool looped)
    {
        var at = (int)part * AxisCount;

        return new EmotePartState(
            Axis(at, 0, tick, looped),
            Axis(at, 1, tick, looped),
            Axis(at, 2, tick, looped),
            Axis(at, 3, tick, looped),
            Axis(at, 4, tick, looped),
            Axis(at, 5, tick, looped));
    }

    private float Axis(int part, int axis, double tick, bool looped)
    {
        var keys = _tracks[part + axis];

        if (keys is null || keys.Length == 0)
        {
            return 0;
        }

        var rest = axis < 3 ? RestPosition[part / AxisCount * 3 + axis] : 0f;
        var at = LastAtOrBefore(keys, tick);
        var before = Before(keys, at, tick, rest);

        // Once a loop has turned back, a part whose latest keyframe lies before the
        // return tick continues from where the pass ended, not from the emote's opening.
        if (looped && before.Tick < ReturnTick)
        {
            before = Before(keys, LastAtOrBefore(keys, EndTick), tick, rest);
        }

        return Between(before, After(keys, at, tick, rest), tick) - rest;
    }

    /// <summary>The keyframe a value is coming from; before a part's first one, standing still.</summary>
    private EmoteKeyframe Before(EmoteKeyframe[] keys, int at, double tick, float rest)
    {
        if (at < 0)
        {
            return new EmoteKeyframe(tick < BeginTick ? 0 : tick < EndTick ? BeginTick : EndTick, rest, VirtualEasing);
        }

        var key = keys[at];

        // A part's last value is held until the end tick, and only then let go: past the
        // end, the keyframe counts as standing there.
        return !IsLoop && tick >= EndTick && at == keys.Length - 1 && key.Tick < EndTick
            ? key with { Tick = EndTick }
            : key;
    }

    /// <summary>The keyframe a value is going to; after a part's last one, the hold and then standing still.</summary>
    private EmoteKeyframe After(EmoteKeyframe[] keys, int at, double tick, float rest)
    {
        if (at + 1 < keys.Length)
        {
            return keys[at + 1];
        }

        if (IsLoop)
        {
            return new EmoteKeyframe(EndTick + 1, rest, VirtualEasing);
        }

        return tick < EndTick
            ? keys[^1] with { Tick = EndTick }
            : new EmoteKeyframe(StopTick, rest, VirtualEasing);
    }

    /// <summary>The easing of the keyframes the mod makes up at the edges of an emote.</summary>
    private const EmoteEasing VirtualEasing = EmoteEasing.InOutSine;

    private float Between(EmoteKeyframe before, EmoteKeyframe after, double tick)
    {
        double from = before.Tick;
        double to = after.Tick;

        // Across the seam of a loop the keyframe behind is the last of the pass before,
        // and the one ahead may be the first of the pass to come: either is a whole
        // cycle away from where its tick says.
        if (from >= to)
        {
            var cycle = EndTick - ReturnTick + 1;

            if (tick < from)
            {
                from -= cycle;
            }
            else
            {
                to += cycle;
            }
        }

        if (from >= to)
        {
            return before.Value;
        }

        var progress = (tick - from) / (to - from);
        var eased = EmoteEasings.Apply(EaseBeforeKeyframe ? after.Easing : before.Easing, progress);

        return (float)(before.Value + (after.Value - before.Value) * eased);
    }

    /// <summary>The last keyframe at or before a tick, or -1. Two keyframes may share a tick; the later wins.</summary>
    private static int LastAtOrBefore(EmoteKeyframe[] keys, double tick)
    {
        var low = 0;
        var high = keys.Length - 1;
        var found = -1;

        while (low <= high)
        {
            var middle = (low + high) / 2;

            if (keys[middle].Tick <= tick)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}

/// <summary>Collects keyframes as a reader meets them, and checks the result is playable.</summary>
internal sealed class EmoteBuilder
{
    /// <summary>More keyframes than any real emote has; a file claiming more is not one.</summary>
    private const int MaxKeyframes = 400_000;

    private readonly List<EmoteKeyframe>?[] _tracks = new List<EmoteKeyframe>?[7 * 6];
    private int _count;

    public int BeginTick { get; set; }

    public int EndTick { get; set; }

    public int? StopTick { get; set; }

    public bool IsLoop { get; set; }

    public int ReturnTick { get; set; }

    public bool EaseBeforeKeyframe { get; set; }

    public void Add(EmotePart part, EmoteAxis axis, int tick, float value, EmoteEasing easing)
    {
        if (tick < 0 || !float.IsFinite(value))
        {
            return;
        }

        if (++_count > MaxKeyframes)
        {
            throw new InvalidDataException("Too many keyframes.");
        }

        var track = _tracks[(int)part * 6 + (int)axis] ??= new List<EmoteKeyframe>();

        // Files list a part's keyframes in order nearly always; when one does not, each
        // goes after the others of its tick, which is where the mod puts it.
        var at = track.Count;

        while (at > 0 && track[at - 1].Tick > tick)
        {
            at--;
        }

        track.Insert(at, new EmoteKeyframe(tick, value, easing));
    }

    /// <summary>The emote, or null when the mod itself would refuse the file.</summary>
    public Emote? Build(string name, string author, string description)
    {
        if (EndTick <= 0)
        {
            return null;
        }

        if (IsLoop && (ReturnTick > EndTick || ReturnTick < 0))
        {
            return null;
        }

        var stop = StopTick is { } given && given > EndTick ? given : EndTick + 3;

        return new Emote(
            name, author, description,
            Math.Clamp(BeginTick, 0, EndTick), EndTick, stop,
            IsLoop, IsLoop ? ReturnTick : 0, EaseBeforeKeyframe,
            _tracks.Select(track => track is { Count: > 0 } ? track.ToArray() : null).ToArray());
    }
}
