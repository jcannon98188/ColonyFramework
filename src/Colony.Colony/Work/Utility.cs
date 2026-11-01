using Colony.Numerics;

namespace Colony.Jobs;

/// <summary>
/// Utility scoring helpers of spec section 9.2 (optional): fixed-point, saturating, allocation free, no
/// transcendentals, so scores are bit-identical everywhere. Scores and considerations live in [0, 1].
/// </summary>
public static class Utility
{
    /// <summary>Maps <paramref name="value"/> in [<paramref name="min"/>, <paramref name="max"/>] to [0, 1], clamped; 1 when the range is empty or inverted.</summary>
    public static Fixed32 Normalize(Fixed32 value, Fixed32 min, Fixed32 max)
    {
        if (max.Raw <= min.Raw)
        {
            return Fixed32.One;
        }

        return Clamp01((value - min) / (max - min));
    }

    /// <summary>1 - x, for a score in [0, 1].</summary>
    public static Fixed32 Invert(Fixed32 x) => Fixed32.One - Clamp01(x);

    /// <summary>x^2: a response curve that discounts low values.</summary>
    public static Fixed32 Quadratic(Fixed32 x)
    {
        x = Clamp01(x);
        return x * x;
    }

    /// <summary>Smoothstep 3x^2 - 2x^3: an S curve flat at both ends.</summary>
    public static Fixed32 Smooth(Fixed32 x)
    {
        x = Clamp01(x);
        var three = Fixed32.FromInt32(3);
        var two = Fixed32.FromInt32(2);
        return x * x * (three - (two * x));
    }

    /// <summary>The weighted mean of <paramref name="scores"/> (0 when there are none or the weights sum to 0).</summary>
    public static Fixed32 WeightedSum(ReadOnlySpan<Fixed32> scores, ReadOnlySpan<Fixed32> weights)
    {
        if (scores.Length != weights.Length)
        {
            throw new ArgumentException("Every score needs one weight.", nameof(weights));
        }

        var sum = Fixed32.Zero;
        var total = Fixed32.Zero;
        for (int i = 0; i < scores.Length; i++)
        {
            sum += scores[i] * weights[i];
            total += weights[i];
        }

        return total.Raw <= 0 ? Fixed32.Zero : Clamp01(sum / total);
    }

    /// <summary>
    /// The compensated product of considerations: the product of the inputs lifted by
    /// <c>(1 - product) * (1 - 1/n) * product</c>, so many moderate considerations do not collapse toward zero
    /// while any zero still vetoes. 0 for no considerations.
    /// </summary>
    public static Fixed32 Combine(ReadOnlySpan<Fixed32> considerations)
    {
        if (considerations.Length == 0)
        {
            return Fixed32.Zero;
        }

        var product = Fixed32.One;
        foreach (var consideration in considerations)
        {
            product *= Clamp01(consideration);
        }

        var modification = Fixed32.One - Fixed32.FromRatio(1, considerations.Length);
        var makeup = (Fixed32.One - product) * modification;
        return Clamp01(product + (makeup * product));
    }

    /// <summary>The index of the highest score; ties go to the earliest; -1 when empty.</summary>
    public static int Best(ReadOnlySpan<Fixed32> scores)
    {
        int best = -1;
        for (int i = 0; i < scores.Length; i++)
        {
            if (best < 0 || scores[i].Raw > scores[best].Raw)
            {
                best = i;
            }
        }

        return best;
    }

    private static Fixed32 Clamp01(Fixed32 x) => Fixed32.Clamp(x, Fixed32.Zero, Fixed32.One);
}
