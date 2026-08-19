namespace MusicTrackGen.Core;

/// <summary>Everything the analyser measures from one reference track.</summary>
public sealed class TrackFeatures
{
    public string SourcePath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public double Bpm { get; set; }
    public int KeyRoot { get; set; }
    public string ModeName { get; set; } = "Major";
    public double KeyConfidence { get; set; }
    public double LoudnessRms { get; set; }
    public double BrightnessHz { get; set; }
    public double[] Chroma { get; set; } = new double[12];
    /// <summary>Measured onset probability per 1/16 bar position, per drum role.</summary>
    public Dictionary<string, double[]> RhythmGrid { get; set; } = new();
    /// <summary>Chord scale degrees, one per beat.</summary>
    public List<int> DegreeSequence { get; set; } = [];
    /// <summary>Relative lengths of detected sections.</summary>
    public double[] FormFractions { get; set; } = [];
    public double LowEnergy { get; set; }
    public double MidEnergy { get; set; }
    public double HighEnergy { get; set; }
    /// <summary>Global tuning deviation from A=440, in cents.</summary>
    public double TuningOffsetCents { get; set; }

    public string Summary() =>
        $"{Bpm:0.0} BPM · {Scales.PitchClassNames[KeyRoot]} {ModeName} " +
        $"(conf {KeyConfidence:0.00}) · RMS {LoudnessRms:0.000} · {BrightnessHz:0} Hz · " +
        $"{FormFractions.Length} sections";
}

/// <summary>
/// Signal analysis for the Train screen: tempo, key/mode, per-beat chords, rhythm grid,
/// spectral balance and song form. All CPU, all in-repo, no model weights.
/// </summary>
public static class Analyser
{
    private const int AnalysisRate = 22050;
    private const int FrameSize = 2048;
    private const int HopSize = 512;

    public static TrackFeatures Analyse(PcmAudio audio, string sourcePath = "")
    {
        float[] mono = Resample(audio.ToMono(), audio.SampleRate, AnalysisRate);
        var features = new TrackFeatures
        {
            SourcePath = sourcePath,
            DurationSeconds = audio.DurationSeconds,
            LoudnessRms = Rms(mono),
        };

        var (spectra, times) = Stft(mono);
        if (spectra.Count < 8)
            throw new InvalidDataException("Track is too short to analyse (need at least ~1 second of audio).");

        var onset = OnsetEnvelope(spectra);
        features.Bpm = EstimateTempo(onset, AnalysisRate / (double)HopSize);

        // Reference pitch varies between recordings (and this app's own generator tunes
        // every track to anything from A=432 to A=444). Estimating that offset first stops
        // a globally detuned track from smearing energy into neighbouring pitch classes.
        features.TuningOffsetCents = EstimateTuningOffsetCents(spectra);

        var chromaFrames = ChromaFrames(spectra, tuningOffsetCents: features.TuningOffsetCents);
        features.Chroma = Average(chromaFrames);
        // A separate bass-range chroma carries the root far more reliably than the full
        // range does, and the root is what distinguishes modes that share a note set.
        var bassChroma = Average(ChromaFrames(spectra, 55, 260, features.TuningOffsetCents));
        var (root, mode, confidence) = EstimateKey(features.Chroma, bassChroma);
        features.KeyRoot = root;
        features.ModeName = mode;
        features.KeyConfidence = confidence;

        features.BrightnessHz = SpectralCentroid(spectra);
        (features.LowEnergy, features.MidEnergy, features.HighEnergy) = BandEnergies(spectra);
        features.RhythmGrid = RhythmGrid(mono, features.Bpm);
        features.DegreeSequence = ChordSequence(chromaFrames, times, features.Bpm, root, mode);
        features.FormFractions = FormFractions(chromaFrames);

        return features;
    }

    // ---------------- basics ----------------

    private static double Rms(float[] x)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        int outLength = (int)((long)input.Length * toRate / fromRate);
        var output = new float[Math.Max(1, outLength)];
        double step = fromRate / (double)toRate;
        for (int i = 0; i < output.Length; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            double frac = pos - i0;
            output[i] = (float)(input[Math.Min(i0, input.Length - 1)] * (1 - frac) + input[i1] * frac);
        }
        return output;
    }

    private static (List<double[]> Spectra, List<double> Times) Stft(float[] mono)
    {
        var window = Fft.HannWindow(FrameSize);
        var spectra = new List<double[]>();
        var times = new List<double>();
        var frame = new double[FrameSize];

        for (int start = 0; start + FrameSize <= mono.Length; start += HopSize)
        {
            for (int i = 0; i < FrameSize; i++) frame[i] = mono[start + i] * window[i];
            spectra.Add(Fft.Magnitude(frame));
            times.Add(start / (double)AnalysisRate);
        }
        return (spectra, times);
    }

    // ---------------- tempo ----------------

    private static double[] OnsetEnvelope(List<double[]> spectra)
    {
        var onset = new double[spectra.Count];
        for (int t = 1; t < spectra.Count; t++)
        {
            double flux = 0;
            var cur = spectra[t];
            var prev = spectra[t - 1];
            for (int k = 0; k < cur.Length; k++)
            {
                double d = cur[k] - prev[k];
                if (d > 0) flux += d;
            }
            onset[t] = flux;
        }

        // Remove the slow trend so loud sections don't dominate the autocorrelation.
        int smooth = 16;
        var detrended = new double[onset.Length];
        for (int i = 0; i < onset.Length; i++)
        {
            double sum = 0;
            int lo = Math.Max(0, i - smooth), hi = Math.Min(onset.Length - 1, i + smooth);
            for (int j = lo; j <= hi; j++) sum += onset[j];
            detrended[i] = Math.Max(0, onset[i] - sum / (hi - lo + 1));
        }
        return detrended;
    }

    private static double EstimateTempo(double[] onset, double framesPerSecond)
    {
        // Autocorrelate the onset envelope; a peak at lag L means a beat every L frames.
        int minLag = (int)(framesPerSecond * 60.0 / 200.0);   // 200 BPM
        int maxLag = (int)(framesPerSecond * 60.0 / 50.0);    // 50 BPM
        minLag = Math.Max(2, minLag);
        maxLag = Math.Min(onset.Length / 2, maxLag);
        if (maxLag <= minLag) return 100;

        double bestScore = double.NegativeInfinity;
        double bestBpm = 100;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < onset.Length; i++) sum += onset[i] * onset[i + lag];
            double bpm = 60.0 * framesPerSecond / lag;

            // Prefer the range most popular music actually sits in, which resolves the
            // classic half/double-tempo ambiguity more often than raw peak picking.
            double prior = Math.Exp(-Math.Pow(Math.Log(bpm / 105.0), 2) / (2 * 0.30 * 0.30));
            double score = sum / (onset.Length - lag) * prior;

            if (score > bestScore) { bestScore = score; bestBpm = bpm; }
        }
        return Math.Clamp(bestBpm, 50, 200);
    }

    // ---------------- pitch content ----------------

    /// <summary>
    /// Finds the track's global tuning by histogramming how far spectral peaks sit from
    /// equal-tempered semitone centres. A consistent bias means the whole recording is
    /// tuned away from A=440.
    /// </summary>
    private static double EstimateTuningOffsetCents(List<double[]> spectra)
    {
        double binHz = AnalysisRate / (double)FrameSize;
        const int Bins = 20;                       // 5-cent resolution across a semitone
        var histogram = new double[Bins];

        int lo = Math.Max(2, (int)(80 / binHz));
        int hi = (int)(1600 / binHz);

        foreach (var mag in spectra)
        {
            int top = Math.Min(mag.Length - 2, hi);
            for (int k = lo; k <= top; k++)
            {
                // Only local peaks carry pitch information; everything else is skirt.
                if (mag[k] <= mag[k - 1] || mag[k] < mag[k + 1]) continue;

                // Parabolic interpolation for a sub-bin frequency estimate.
                double alpha = mag[k - 1], beta = mag[k], gamma = mag[k + 1];
                double denom = alpha - 2 * beta + gamma;
                double shift = Math.Abs(denom) < 1e-12 ? 0 : 0.5 * (alpha - gamma) / denom;
                double freq = (k + Math.Clamp(shift, -0.5, 0.5)) * binHz;
                if (freq <= 0) continue;

                double midi = 69 + 12 * Math.Log2(freq / 440.0);
                double cents = (midi - Math.Round(midi)) * 100.0;      // -50..+50
                int bin = (int)Math.Floor((cents + 50.0) / 100.0 * Bins);
                histogram[Math.Clamp(bin, 0, Bins - 1)] += beta;
            }
        }

        double total = histogram.Sum();
        if (total < 1e-9) return 0;

        // Circular centre of mass: the histogram wraps at +/-50 cents.
        double sinSum = 0, cosSum = 0;
        for (int i = 0; i < Bins; i++)
        {
            double angle = 2 * Math.PI * i / Bins;
            sinSum += histogram[i] * Math.Sin(angle);
            cosSum += histogram[i] * Math.Cos(angle);
        }
        double meanBin = Math.Atan2(sinSum, cosSum) / (2 * Math.PI) * Bins;
        if (meanBin < 0) meanBin += Bins;
        return Math.Clamp(meanBin / Bins * 100.0 - 50.0, -50, 50);
    }

    private static List<double[]> ChromaFrames(List<double[]> spectra, double lowHz = 55,
                                              double highHz = 2000, double tuningOffsetCents = 0)
    {
        var frames = new List<double[]>(spectra.Count);
        double binHz = AnalysisRate / (double)FrameSize;
        double referenceHz = 440.0 * Math.Pow(2.0, tuningOffsetCents / 1200.0);

        foreach (var mag in spectra)
        {
            var chroma = new double[12];
            // 55 Hz to 2 kHz covers the pitched content without picking up much noise.
            int lo = Math.Max(1, (int)(lowHz / binHz));
            int hi = Math.Min(mag.Length - 1, (int)(highHz / binHz));
            for (int k = lo; k <= hi; k++)
            {
                double freq = k * binHz;
                double midi = 69 + 12 * Math.Log2(freq / referenceHz);
                int pc = ((int)Math.Round(midi) % 12 + 12) % 12;
                chroma[pc] += mag[k];
            }
            double sum = chroma.Sum();
            if (sum > 1e-9)
                for (int i = 0; i < 12; i++) chroma[i] /= sum;
            frames.Add(chroma);
        }
        return frames;
    }

    private static double[] Average(List<double[]> frames)
    {
        var avg = new double[12];
        if (frames.Count == 0) return avg;
        foreach (var f in frames)
            for (int i = 0; i < 12; i++) avg[i] += f[i];
        for (int i = 0; i < 12; i++) avg[i] /= frames.Count;
        double sum = avg.Sum();
        if (sum > 1e-9) for (int i = 0; i < 12; i++) avg[i] /= sum;
        return avg;
    }

    /// <summary>
    /// Correlates the chroma against every scale in the library at every root. Ragas are
    /// candidates alongside major/minor, which is what lets an Indian-music library train
    /// a profile that actually reports Kafi or Khamaj rather than being forced into "minor".
    /// </summary>
    private static (int Root, string Mode, double Confidence) EstimateKey(double[] chroma, double[] bassChroma)
    {
        double best = double.NegativeInfinity, second = double.NegativeInfinity;
        int bestRoot = 0;
        string bestMode = "Major";

        double bassPeak = bassChroma.Max();

        foreach (var scale in Scales.All)
        {
            var template = GradedTemplate(scale);

            for (int root = 0; root < 12; root++)
            {
                var rotated = new double[12];
                for (int i = 0; i < 12; i++) rotated[i] = chroma[(i + root) % 12];
                double score = Correlate(rotated, template);

                // Modes that share a note set (E Dorian, B Aeolian and D Ionian all use the
                // same seven pitches) are indistinguishable from note content alone. What
                // separates them is which pitch behaves as the root, and the bass says so.
                if (bassPeak > 1e-9) score += 0.30 * (bassChroma[root] / bassPeak);

                if (score > best)
                {
                    second = best;
                    best = score;
                    bestRoot = root;
                    bestMode = scale.Name;
                }
                else if (score > second) second = score;
            }
        }

        double confidence = best <= 0 ? 0 : Math.Clamp((best - second) / Math.Abs(best) * 4.0, 0, 1);
        return (bestRoot, bestMode, confidence);
    }

    /// <summary>
    /// Graded key profile in the spirit of Krumhansl-Kessler: scale members are not equal,
    /// and non-members get a low but non-zero weight because real recordings contain
    /// passing tones. A graded profile discriminates far better than a binary mask, which
    /// scores every rotation sharing six of seven notes almost identically.
    /// </summary>
    private static double[] GradedTemplate(ScaleDef scale)
    {
        var template = new double[12];
        for (int i = 0; i < 12; i++) template[i] = 1.6;                 // out-of-scale floor

        for (int degree = 0; degree < scale.Semitones.Length; degree++)
        {
            int pc = scale.Semitones[degree] % 12;
            template[pc] = degree switch
            {
                0 => 6.4,                                              // tonic
                _ => pc == 7 ? 5.2 : 3.4,                              // fifth, then the rest
            };
        }
        // The third defines the mode's colour, so give it more weight than a filler degree.
        int thirdIndex = scale.Semitones.Length >= 7 ? 2 : 1;
        if (thirdIndex < scale.Semitones.Length)
            template[scale.Semitones[thirdIndex] % 12] = 4.4;

        return template;
    }

    /// <summary>Pearson correlation, which ignores overall level and offset differences.</summary>
    private static double Correlate(double[] a, double[] b)
    {
        double meanA = a.Average(), meanB = b.Average();
        double num = 0, denA = 0, denB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - meanA, db = b[i] - meanB;
            num += da * db;
            denA += da * da;
            denB += db * db;
        }
        double den = Math.Sqrt(denA * denB);
        return den < 1e-12 ? 0 : num / den;
    }

    /// <summary>
    /// The set of pitch classes a (root, scale) pair uses. Two keys with the same set are
    /// rotations of one another, which is the ambiguity key detection cannot resolve from
    /// note content alone.
    /// </summary>
    public static SortedSet<int> PitchClassSet(int root, string modeName)
    {
        var scale = Scales.ByName(modeName);
        return new SortedSet<int>(scale.Semitones.Select(s => ((s + root) % 12 + 12) % 12));
    }

    /// <summary>Per-beat chord estimate, expressed as a scale degree of the detected key.</summary>
    private static List<int> ChordSequence(List<double[]> chromaFrames, List<double> times,
                                          double bpm, int root, string modeName)
    {
        var scale = Scales.ByName(modeName);
        int degrees = Math.Min(7, scale.Length);
        var sequence = new List<int>();
        if (chromaFrames.Count == 0) return sequence;

        double beatSeconds = 60.0 / Math.Max(30.0, bpm);
        double end = times[^1];

        for (double t = 0; t + beatSeconds <= end; t += beatSeconds)
        {
            // Average chroma across this beat.
            var beatChroma = new double[12];
            int count = 0;
            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] < t) continue;
                if (times[i] >= t + beatSeconds) break;
                for (int p = 0; p < 12; p++) beatChroma[p] += chromaFrames[i][p];
                count++;
            }
            if (count == 0) continue;
            for (int p = 0; p < 12; p++) beatChroma[p] /= count;

            // Which of the key's triads best explains this beat?
            int bestDegree = 0;
            double bestScore = double.NegativeInfinity;
            for (int degree = 0; degree < degrees; degree++)
            {
                int[] triad = Chords.Triad(scale, 60 + root, degree);
                double score = triad.Sum(note => beatChroma[((note % 12) + 12) % 12]);
                if (score > bestScore) { bestScore = score; bestDegree = degree; }
            }
            sequence.Add(bestDegree);
        }
        return sequence;
    }

    // ---------------- rhythm and timbre ----------------

    private static double SpectralCentroid(List<double[]> spectra)
    {
        double binHz = AnalysisRate / (double)FrameSize;
        double weighted = 0, total = 0;
        foreach (var mag in spectra)
            for (int k = 1; k < mag.Length; k++)
            {
                weighted += k * binHz * mag[k];
                total += mag[k];
            }
        return total < 1e-9 ? 1500 : weighted / total;
    }

    private static (double Low, double Mid, double High) BandEnergies(List<double[]> spectra)
    {
        double binHz = AnalysisRate / (double)FrameSize;
        double low = 0, mid = 0, high = 0;
        foreach (var mag in spectra)
            for (int k = 1; k < mag.Length; k++)
            {
                double f = k * binHz;
                double e = mag[k] * mag[k];
                if (f < 250) low += e;
                else if (f < 4000) mid += e;
                else high += e;
            }
        double total = low + mid + high;
        return total < 1e-9 ? (0.33, 0.34, 0.33) : (low / total, mid / total, high / total);
    }

    /// <summary>
    /// Onset strength per 1/16 position within the bar, split into three frequency bands
    /// that stand in for kick / snare-and-hand-drum / hat.
    /// </summary>
    private static Dictionary<string, double[]> RhythmGrid(float[] mono, double bpm)
    {
        var bands = new (string Name, Biquad Filter)[]
        {
            ("kick", Biquad.LowPass(AnalysisRate, 150, 0.8)),
            ("snare", Biquad.BandPass(AnalysisRate, 1800, 0.8)),
            ("hat", Biquad.HighPass(AnalysisRate, 7000, 0.8)),
        };

        double beatSamples = 60.0 / Math.Max(30.0, bpm) * AnalysisRate;
        double stepSamples = beatSamples / 4.0;              // 1/16 note
        int stepsPerBar = 16;
        var result = new Dictionary<string, double[]>();

        foreach (var (name, filter) in bands)
        {
            var filtered = new float[mono.Length];
            for (int i = 0; i < mono.Length; i++) filtered[i] = (float)filter.Process(mono[i]);

            var accumulator = new double[stepsPerBar];
            var counts = new int[stepsPerBar];
            int totalSteps = (int)(filtered.Length / stepSamples);

            for (int s = 0; s < totalSteps; s++)
            {
                int from = (int)(s * stepSamples);
                int to = Math.Min(filtered.Length, (int)((s + 1) * stepSamples));
                if (to <= from) continue;
                double peak = 0;
                for (int i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(filtered[i]));
                int slot = s % stepsPerBar;
                accumulator[slot] += peak;
                counts[slot]++;
            }

            var grid = new double[stepsPerBar];
            double max = 0;
            for (int i = 0; i < stepsPerBar; i++)
            {
                grid[i] = counts[i] > 0 ? accumulator[i] / counts[i] : 0;
                max = Math.Max(max, grid[i]);
            }
            if (max > 1e-9)
                for (int i = 0; i < stepsPerBar; i++)
                    // Normalise to a probability, with a floor so a quiet step is unlikely
                    // rather than impossible.
                    grid[i] = Math.Clamp(grid[i] / max * 0.95, 0.02, 0.98);
            result[name] = grid;
        }
        return result;
    }

    /// <summary>
    /// Section boundaries from a chroma novelty curve: where the harmonic content changes
    /// sharply, a section changed. Returns the relative lengths of the detected sections.
    /// </summary>
    private static double[] FormFractions(List<double[]> chromaFrames)
    {
        int window = 86;                                // ~2 s at 43 fps
        if (chromaFrames.Count < window * 3) return [];

        var novelty = new double[chromaFrames.Count];
        for (int t = window; t < chromaFrames.Count - window; t++)
        {
            var before = new double[12];
            var after = new double[12];
            for (int i = 0; i < window; i++)
                for (int p = 0; p < 12; p++)
                {
                    before[p] += chromaFrames[t - 1 - i][p];
                    after[p] += chromaFrames[t + i][p];
                }
            double distance = 0;
            for (int p = 0; p < 12; p++)
            {
                double d = (after[p] - before[p]) / window;
                distance += d * d;
            }
            novelty[t] = Math.Sqrt(distance);
        }

        // Greedy peak picking with a minimum spacing so we get sections, not flutter.
        var boundaries = new List<int>();
        int minSpacing = window * 2;
        var order = Enumerable.Range(0, novelty.Length).OrderByDescending(i => novelty[i]);
        foreach (int candidate in order)
        {
            if (novelty[candidate] <= 0) break;
            if (boundaries.Count >= 7) break;
            if (boundaries.All(b => Math.Abs(b - candidate) >= minSpacing)) boundaries.Add(candidate);
        }
        if (boundaries.Count == 0) return [];

        boundaries.Sort();
        var lengths = new List<double>();
        int previous = 0;
        foreach (int b in boundaries)
        {
            lengths.Add(b - previous);
            previous = b;
        }
        lengths.Add(chromaFrames.Count - previous);

        double total = lengths.Sum();
        return total <= 0 ? [] : lengths.Select(l => l / total).ToArray();
    }
}
