using System;

namespace RobustoClient.Systems.Aim;

/// <summary>
/// Smoothed estimate of how far the rendered world lags the server, in ticks.
/// </summary>
/// <remarks>
/// Lidgren's <c>Ping</c> is a whole-millisecond average that does not account for the Windows timer
/// period, which is why the engine itself carries <c>net.predict_lag_bias</c>. We therefore sample the
/// engine's own render skew instead and filter it here: an EWMA for the typical value, a sliding peak for
/// spikes, and a bias toward the peak when the state buffer is starving, because a client that has fallen
/// behind its buffer has a genuinely larger skew than its average.
/// </remarks>
internal sealed class RobustaLeadEstimator
{
    private const int SampleWindow = 30;
    private const float MeanAlpha = 0.2f;
    private const float PeakWeight = 0.6f;
    private const float DeadBandTicks = 1f;

    private readonly float[] _samples = new float[SampleWindow];

    private int _count;
    private int _next;
    private float _mean;
    private float _applied;

    /// <summary>Current estimate, in ticks, with the dead band already applied.</summary>
    public float AppliedTicks => _applied;

    public void Sample(float lagTicks, bool starving)
    {
        if (!float.IsFinite(lagTicks))
            return;

        lagTicks = MathF.Max(0f, lagTicks);

        _samples[_next] = lagTicks;
        _next = (_next + 1) % SampleWindow;
        if (_count < SampleWindow)
            _count++;

        _mean = _count == 1 ? lagTicks : _mean + (lagTicks - _mean) * MeanAlpha;

        var peak = Peak();
        var target = starving ? peak : _mean + (peak - _mean) * PeakWeight;

        // Sub-tick movement would make the aim point twitch between frames.
        if (MathF.Abs(target - _applied) >= DeadBandTicks || _count == 1)
            _applied = target;
    }

    public float LeadSeconds(TimeSpan tickPeriod, float maxSeconds)
    {
        var seconds = _applied * (float) tickPeriod.TotalSeconds;
        return Math.Clamp(seconds, 0f, MathF.Max(0f, maxSeconds));
    }

    public void Reset()
    {
        Array.Clear(_samples);
        _count = 0;
        _next = 0;
        _mean = 0f;
        _applied = 0f;
    }

    private float Peak()
    {
        var peak = 0f;
        for (var i = 0; i < _count; i++)
            peak = MathF.Max(peak, _samples[i]);

        return peak;
    }
}
