namespace MusicTrackGen.Core;

/// <summary>Two-pole IIR filter (Robert Bristow-Johnson cookbook coefficients).</summary>
public sealed class Biquad
{
    private double _a0, _a1, _a2, _b1, _b2;
    private double _x1, _x2, _y1, _y2;

    public static Biquad LowPass(double sampleRate, double freq, double q = 0.707)
    {
        var f = new Biquad();
        double w0 = 2 * Math.PI * Clamp(freq, sampleRate) / sampleRate;
        double alpha = Math.Sin(w0) / (2 * q);
        double cos = Math.Cos(w0);
        double b0 = (1 - cos) / 2, b1 = 1 - cos, b2 = (1 - cos) / 2;
        double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
        f.Set(b0, b1, b2, a0, a1, a2);
        return f;
    }

    public static Biquad HighPass(double sampleRate, double freq, double q = 0.707)
    {
        var f = new Biquad();
        double w0 = 2 * Math.PI * Clamp(freq, sampleRate) / sampleRate;
        double alpha = Math.Sin(w0) / (2 * q);
        double cos = Math.Cos(w0);
        double b0 = (1 + cos) / 2, b1 = -(1 + cos), b2 = (1 + cos) / 2;
        double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
        f.Set(b0, b1, b2, a0, a1, a2);
        return f;
    }

    /// <summary>Constant-skirt band pass; peak gain equals q. Used for vowel formants.</summary>
    public static Biquad BandPass(double sampleRate, double freq, double q)
    {
        var f = new Biquad();
        double w0 = 2 * Math.PI * Clamp(freq, sampleRate) / sampleRate;
        double alpha = Math.Sin(w0) / (2 * q);
        double cos = Math.Cos(w0);
        double b0 = alpha, b1 = 0, b2 = -alpha;
        double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
        f.Set(b0, b1, b2, a0, a1, a2);
        return f;
    }

    public static Biquad HighShelf(double sampleRate, double freq, double gainDb, double slope = 1.0)
    {
        var f = new Biquad();
        double A = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * Clamp(freq, sampleRate) / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / 2 * Math.Sqrt((A + 1 / A) * (1 / slope - 1) + 2);
        double twoRootAAlpha = 2 * Math.Sqrt(A) * alpha;
        double b0 = A * ((A + 1) + (A - 1) * cos + twoRootAAlpha);
        double b1 = -2 * A * ((A - 1) + (A + 1) * cos);
        double b2 = A * ((A + 1) + (A - 1) * cos - twoRootAAlpha);
        double a0 = (A + 1) - (A - 1) * cos + twoRootAAlpha;
        double a1 = 2 * ((A - 1) - (A + 1) * cos);
        double a2 = (A + 1) - (A - 1) * cos - twoRootAAlpha;
        f.Set(b0, b1, b2, a0, a1, a2);
        return f;
    }

    private static double Clamp(double freq, double sampleRate) =>
        Math.Clamp(freq, 20.0, sampleRate * 0.47);

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _a0 = b0 / a0; _a1 = b1 / a0; _a2 = b2 / a0;
        _b1 = a1 / a0; _b2 = a2 / a0;
    }

    public double Process(double x)
    {
        double y = _a0 * x + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return y;
    }

    public void ProcessInPlace(float[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (float)Process(buffer[i]);
    }

    public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }
}

/// <summary>Schroeder reverb: parallel comb filters into series allpass sections.</summary>
public sealed class Reverb
{
    private readonly float[][] _combBuf;
    private readonly int[] _combIdx;
    private readonly double[] _combFeedback;
    private readonly float[][] _apBuf;
    private readonly int[] _apIdx;
    private readonly Biquad _damp;

    public Reverb(int sampleRate, double roomSeconds = 1.9, double offset = 1.0)
    {
        // Mutually prime-ish comb delays, scaled per channel so L/R decorrelate.
        double[] combMs = [29.7, 37.1, 41.1, 43.7];
        double[] apMs = [5.0, 1.7, 0.5];

        _combBuf = new float[combMs.Length][];
        _combIdx = new int[combMs.Length];
        _combFeedback = new double[combMs.Length];
        for (int i = 0; i < combMs.Length; i++)
        {
            int n = Math.Max(1, (int)(combMs[i] * offset * sampleRate / 1000.0));
            _combBuf[i] = new float[n];
            double delaySeconds = n / (double)sampleRate;
            _combFeedback[i] = Math.Pow(0.001, delaySeconds / Math.Max(0.2, roomSeconds));
        }

        _apBuf = new float[apMs.Length][];
        _apIdx = new int[apMs.Length];
        for (int i = 0; i < apMs.Length; i++)
            _apBuf[i] = new float[Math.Max(1, (int)(apMs[i] * offset * sampleRate / 1000.0))];

        _damp = Biquad.LowPass(sampleRate, 5200, 0.7);
    }

    public double Process(double input)
    {
        double sum = 0;
        for (int i = 0; i < _combBuf.Length; i++)
        {
            var buf = _combBuf[i];
            int idx = _combIdx[i];
            double delayed = buf[idx];
            sum += delayed;
            buf[idx] = (float)(input + delayed * _combFeedback[i]);
            _combIdx[i] = (idx + 1) % buf.Length;
        }
        sum = _damp.Process(sum / _combBuf.Length);

        for (int i = 0; i < _apBuf.Length; i++)
        {
            var buf = _apBuf[i];
            int idx = _apIdx[i];
            double delayed = buf[idx];
            double output = -sum + delayed;
            buf[idx] = (float)(sum + delayed * 0.5);
            _apIdx[i] = (idx + 1) % buf.Length;
            sum = output;
        }
        return sum;
    }
}

public sealed class Delay
{
    private readonly float[] _buf;
    private int _idx;
    private readonly double _feedback;

    public Delay(int sampleRate, double delayMs, double feedback)
    {
        _buf = new float[Math.Max(1, (int)(delayMs * sampleRate / 1000.0))];
        _feedback = feedback;
    }

    public double Process(double input)
    {
        double delayed = _buf[_idx];
        _buf[_idx] = (float)(input + delayed * _feedback);
        _idx = (_idx + 1) % _buf.Length;
        return delayed;
    }
}

public static class Dynamics
{
    /// <summary>Feed-forward compressor with RMS-ish envelope following.</summary>
    public static void Compress(float[] buffer, int sampleRate, double thresholdDb, double ratio,
                                double attackMs, double releaseMs, double makeupDb)
    {
        double threshold = DbToLin(thresholdDb);
        double attack = Math.Exp(-1.0 / (attackMs * 0.001 * sampleRate));
        double release = Math.Exp(-1.0 / (releaseMs * 0.001 * sampleRate));
        double makeup = DbToLin(makeupDb);
        double env = 0;

        for (int i = 0; i < buffer.Length; i++)
        {
            double x = Math.Abs(buffer[i]);
            env = x > env ? attack * env + (1 - attack) * x : release * env + (1 - release) * x;

            double gain = 1.0;
            if (env > threshold && env > 1e-9)
            {
                double over = env / threshold;
                gain = Math.Pow(over, 1.0 / ratio - 1.0);
            }
            buffer[i] = (float)(buffer[i] * gain * makeup);
        }
    }

    /// <summary>Soft-knee peak limiter; keeps the master under the ceiling without hard clipping.</summary>
    public static void Limit(float[] buffer, double ceiling = 0.891)   // -1 dBFS
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            double x = buffer[i];
            double a = Math.Abs(x);
            if (a > ceiling * 0.7)
            {
                double t = ceiling * 0.7;
                double over = (a - t) / (1.0 - t + 1e-9);
                double shaped = t + (ceiling - t) * Math.Tanh(over);
                x = Math.Sign(x) * shaped;
            }
            buffer[i] = (float)Math.Clamp(x, -ceiling, ceiling);
        }
    }

    /// <summary>Scales to a target RMS, then guarantees the peak ceiling.</summary>
    public static double NormaliseRms(float[] left, float[] right, double targetRms, double peakCeiling = 0.95)
    {
        double sum = 0;
        for (int i = 0; i < left.Length; i++) sum += left[i] * left[i] + right[i] * right[i];
        double rms = Math.Sqrt(sum / Math.Max(1, left.Length * 2));
        if (rms < 1e-7) return 1.0;

        double gain = targetRms / rms;
        double peak = 0;
        for (int i = 0; i < left.Length; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        if (peak * gain > peakCeiling) gain = peakCeiling / Math.Max(1e-9, peak);

        for (int i = 0; i < left.Length; i++)
        {
            left[i] = (float)(left[i] * gain);
            right[i] = (float)(right[i] * gain);
        }
        return gain;
    }

    public static double DbToLin(double db) => Math.Pow(10, db / 20.0);

    public static double LinToDb(double lin) => 20.0 * Math.Log10(Math.Max(1e-12, lin));

    /// <summary>Integrated RMS level of a rendered mix, reported in the sidecar.</summary>
    public static double Rms(float[] left, float[] right)
    {
        double sum = 0;
        for (int i = 0; i < left.Length; i++) sum += left[i] * left[i] + right[i] * right[i];
        return Math.Sqrt(sum / Math.Max(1, left.Length * 2));
    }
}

/// <summary>Karplus-Strong plucked string, used for the sitar-like and guitar-like parts.</summary>
public sealed class PluckedString
{
    private readonly float[] _buf;
    private int _idx;
    private readonly double _damping;
    private double _last;

    public PluckedString(int sampleRate, double frequency, double damping, Rng rng)
    {
        int n = Math.Max(2, (int)(sampleRate / Math.Max(20.0, frequency)));
        _buf = new float[n];
        for (int i = 0; i < n; i++) _buf[i] = (float)(rng.NextDouble() * 2 - 1);
        _damping = Math.Clamp(damping, 0.9, 0.999);
    }

    public double Next()
    {
        double current = _buf[_idx];
        double filtered = 0.5 * (current + _last) * _damping;
        _last = current;
        _buf[_idx] = (float)filtered;
        _idx = (_idx + 1) % _buf.Length;
        return filtered;
    }
}

/// <summary>ADSR envelope evaluated in seconds.</summary>
public readonly struct Adsr(double attack, double decay, double sustain, double release)
{
    public double Attack { get; } = attack;
    public double Decay { get; } = decay;
    public double Sustain { get; } = sustain;
    public double Release { get; } = release;

    /// <summary>Envelope value at time t for a note held for holdSeconds.</summary>
    public double At(double t, double holdSeconds)
    {
        if (t < 0) return 0;
        if (t < Attack) return t / Math.Max(1e-6, Attack);
        if (t < Attack + Decay)
        {
            double k = (t - Attack) / Math.Max(1e-6, Decay);
            return 1.0 - (1.0 - Sustain) * k;
        }
        if (t < holdSeconds) return Sustain;
        double r = (t - holdSeconds) / Math.Max(1e-6, Release);
        return r >= 1 ? 0 : Sustain * (1 - r) * (1 - r);
    }

    public double TotalSeconds(double holdSeconds) => Math.Max(holdSeconds, Attack + Decay) + Release;
}
