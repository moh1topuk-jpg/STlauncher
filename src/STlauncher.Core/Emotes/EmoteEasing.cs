namespace STlauncher.Core.Emotes;

/// <summary>
/// How a value travels from one keyframe to the next. The numbers are the ones the
/// binary emote format stores, so a byte read from a file is the enum value itself.
/// </summary>
public enum EmoteEasing : byte
{
    Linear = 0,

    /// <summary>No travel at all: the value holds until the next keyframe replaces it.</summary>
    Constant = 1,

    InSine = 6,
    OutSine = 7,
    InOutSine = 8,
    InCubic = 9,
    OutCubic = 10,
    InOutCubic = 11,
    InQuad = 12,
    OutQuad = 13,
    InOutQuad = 14,
    InQuart = 15,
    OutQuart = 16,
    InOutQuart = 17,
    InQuint = 18,
    OutQuint = 19,
    InOutQuint = 20,
    InExpo = 21,
    OutExpo = 22,
    InOutExpo = 23,
    InCirc = 24,
    OutCirc = 25,
    InOutCirc = 26,
    InBack = 27,
    OutBack = 28,
    InOutBack = 29,
    InElastic = 30,
    OutElastic = 31,
    InOutElastic = 32,
    InBounce = 33,
    OutBounce = 34,
    InOutBounce = 35
}

public static class EmoteEasings
{
    private const byte StepId = 37;

    /// <summary>
    /// Reads an easing the way the mod does: any case, with or without the "ease" in
    /// front ("EASEINOUTQUAD", "InOutSine", "inoutcirc"). An unknown name is linear,
    /// which is what the mod falls back to as well.
    /// </summary>
    public static EmoteEasing Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return EmoteEasing.Linear;
        }

        var key = name.Trim().Replace("_", string.Empty).ToUpperInvariant();

        if (key.StartsWith("EASE", StringComparison.Ordinal))
        {
            key = key[4..];
        }

        if (key is "CONSTANT" or "STEP")
        {
            return EmoteEasing.Constant;
        }

        // Enum.TryParse takes numbers too, and "12" is not the name of anything.
        if (key.Length == 0 || !char.IsLetter(key[0]))
        {
            return EmoteEasing.Linear;
        }

        return Enum.TryParse<EmoteEasing>(key, ignoreCase: true, out var easing) && Enum.IsDefined(easing)
            ? easing
            : EmoteEasing.Linear;
    }

    /// <summary>The easing behind a byte of the binary format.</summary>
    public static EmoteEasing FromId(byte id)
    {
        if (id == StepId)
        {
            return EmoteEasing.Constant;
        }

        var easing = (EmoteEasing)id;
        return Enum.IsDefined(easing) ? easing : EmoteEasing.Linear;
    }

    /// <summary>Progress between two keyframes, 0 to 1, bent by the easing.</summary>
    public static double Apply(EmoteEasing easing, double t)
    {
        t = Math.Clamp(t, 0, 1);

        switch (easing)
        {
            case EmoteEasing.Constant:
                return 0;

            case EmoteEasing.InSine:
                return 1 - Math.Cos(t * Math.PI / 2);
            case EmoteEasing.OutSine:
                return Math.Sin(t * Math.PI / 2);
            case EmoteEasing.InOutSine:
                return -(Math.Cos(Math.PI * t) - 1) / 2;

            case EmoteEasing.InQuad:
                return In(t, 2);
            case EmoteEasing.OutQuad:
                return Out(t, 2);
            case EmoteEasing.InOutQuad:
                return InOut(t, 2);
            case EmoteEasing.InCubic:
                return In(t, 3);
            case EmoteEasing.OutCubic:
                return Out(t, 3);
            case EmoteEasing.InOutCubic:
                return InOut(t, 3);
            case EmoteEasing.InQuart:
                return In(t, 4);
            case EmoteEasing.OutQuart:
                return Out(t, 4);
            case EmoteEasing.InOutQuart:
                return InOut(t, 4);
            case EmoteEasing.InQuint:
                return In(t, 5);
            case EmoteEasing.OutQuint:
                return Out(t, 5);
            case EmoteEasing.InOutQuint:
                return InOut(t, 5);

            case EmoteEasing.InExpo:
                return t <= 0 ? 0 : Math.Pow(2, 10 * t - 10);
            case EmoteEasing.OutExpo:
                return t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);
            case EmoteEasing.InOutExpo:
                if (t <= 0)
                {
                    return 0;
                }

                if (t >= 1)
                {
                    return 1;
                }

                return t < 0.5 ? Math.Pow(2, 20 * t - 10) / 2 : (2 - Math.Pow(2, -20 * t + 10)) / 2;

            case EmoteEasing.InCirc:
                return 1 - Math.Sqrt(1 - t * t);
            case EmoteEasing.OutCirc:
                return Math.Sqrt(1 - (t - 1) * (t - 1));
            case EmoteEasing.InOutCirc:
                return t < 0.5
                    ? (1 - Math.Sqrt(1 - 4 * t * t)) / 2
                    : (Math.Sqrt(1 - (-2 * t + 2) * (-2 * t + 2)) + 1) / 2;

            case EmoteEasing.InBack:
                return (Overshoot + 1) * t * t * t - Overshoot * t * t;
            case EmoteEasing.OutBack:
                return 1 + (Overshoot + 1) * Math.Pow(t - 1, 3) + Overshoot * Math.Pow(t - 1, 2);
            case EmoteEasing.InOutBack:
            {
                const double c = Overshoot * 1.525;
                return t < 0.5
                    ? 4 * t * t * ((c + 1) * 2 * t - c) / 2
                    : (Math.Pow(2 * t - 2, 2) * ((c + 1) * (2 * t - 2) + c) + 2) / 2;
            }

            case EmoteEasing.InElastic:
                return t <= 0 || t >= 1
                    ? t
                    : -Math.Pow(2, 10 * t - 10) * Math.Sin((10 * t - 10.75) * (2 * Math.PI / 3));
            case EmoteEasing.OutElastic:
                return t <= 0 || t >= 1
                    ? t
                    : Math.Pow(2, -10 * t) * Math.Sin((10 * t - 0.75) * (2 * Math.PI / 3)) + 1;
            case EmoteEasing.InOutElastic:
            {
                if (t <= 0 || t >= 1)
                {
                    return t;
                }

                var wave = Math.Sin((20 * t - 11.125) * (2 * Math.PI / 4.5));
                return t < 0.5
                    ? -(Math.Pow(2, 20 * t - 10) * wave) / 2
                    : Math.Pow(2, -20 * t + 10) * wave / 2 + 1;
            }

            case EmoteEasing.InBounce:
                return 1 - Bounce(1 - t);
            case EmoteEasing.OutBounce:
                return Bounce(t);
            case EmoteEasing.InOutBounce:
                return t < 0.5 ? (1 - Bounce(1 - 2 * t)) / 2 : (1 + Bounce(2 * t - 1)) / 2;

            default:
                return t;
        }
    }

    private const double Overshoot = 1.70158;

    private static double In(double t, int power) => Math.Pow(t, power);

    private static double Out(double t, int power) => 1 - Math.Pow(1 - t, power);

    private static double InOut(double t, int power)
        => t < 0.5 ? Math.Pow(2, power - 1) * Math.Pow(t, power) : 1 - Math.Pow(-2 * t + 2, power) / 2;

    private static double Bounce(double t)
    {
        const double n = 7.5625;
        const double d = 2.75;

        if (t < 1 / d)
        {
            return n * t * t;
        }

        if (t < 2 / d)
        {
            t -= 1.5 / d;
            return n * t * t + 0.75;
        }

        if (t < 2.5 / d)
        {
            t -= 2.25 / d;
            return n * t * t + 0.9375;
        }

        t -= 2.625 / d;
        return n * t * t + 0.984375;
    }
}
