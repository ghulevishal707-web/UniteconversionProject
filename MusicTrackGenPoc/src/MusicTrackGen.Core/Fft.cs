namespace MusicTrackGen.Core;

/// <summary>In-place iterative radix-2 FFT. Sizes must be powers of two.</summary>
public static class Fft
{
    public static void Forward(double[] real, double[] imag)
    {
        int n = real.Length;
        if (n <= 1) return;
        if ((n & (n - 1)) != 0)
            throw new ArgumentException($"FFT length must be a power of two, got {n}.");

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            double wReal = Math.Cos(angle), wImag = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double curReal = 1.0, curImag = 0.0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tReal = real[b] * curReal - imag[b] * curImag;
                    double tImag = real[b] * curImag + imag[b] * curReal;
                    real[b] = real[a] - tReal;
                    imag[b] = imag[a] - tImag;
                    real[a] += tReal;
                    imag[a] += tImag;

                    double nextReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = nextReal;
                }
            }
        }
    }

    /// <summary>Magnitude spectrum of the first half (real input assumed).</summary>
    public static double[] Magnitude(double[] windowed)
    {
        int n = windowed.Length;
        var re = (double[])windowed.Clone();
        var im = new double[n];
        Forward(re, im);
        var mag = new double[n / 2];
        for (int i = 0; i < mag.Length; i++) mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return mag;
    }

    public static double[] HannWindow(int size)
    {
        var w = new double[size];
        for (int i = 0; i < size; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
        return w;
    }

    public static int NextPowerOfTwo(int value)
    {
        int p = 1;
        while (p < value) p <<= 1;
        return p;
    }
}
