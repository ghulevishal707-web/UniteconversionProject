# Music Track Generator — PoC application

A Windows desktop application that generates original music tracks with sung vocals in
**English, Marathi, Hindi and Punjabi**, in a **male, female or child** voice, at a
**length you choose**, and learns its musical style from **your own reference tracks**.

This is the working implementation of the design in
[`../docs/music-track-generator-poc.md`](../docs/music-track-generator-poc.md).

---

## What actually runs

| | |
|---|---|
| Desktop app | `MusicTrackGen.App.exe` — WPF, .NET 10, four tabs (Generate / Library / Train / Settings) |
| Console harness | `mtgen.exe` — same engine, scriptable, plus a `selftest` command |
| NuGet dependencies | **none** |
| Sample libraries needed | **none** |
| Network needed at runtime | **no** |

Every oscillator, filter, reverb, drum, the singing voice, the FFT, the WAV reader and
writer, and the analyser are implemented in `MusicTrackGen.Core`. That is a deliberate
choice: there is no SoundFont to license, no voice pack to install, no model weights to
download, and no restore step that can fail behind a corporate proxy.

---

## Build and run

**Prerequisite:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
cd MusicTrackGenPoc

# Verify the engine first — 44 checks, about 50 seconds
dotnet run --project src\MusicTrackGen.Cli -c Release -- selftest

# Launch the desktop app
dotnet run --project src\MusicTrackGen.App -c Release

# Or produce a distributable exe in .\publish\app\
.\publish.ps1                 # ~2 MB, needs the .NET 10 Desktop Runtime on the target machine
.\publish.ps1 -SelfContained  # ~160 MB, carries its own runtime
```

`MusicTrackGen.sln` opens in Visual Studio. Set **MusicTrackGen.App** as the startup project.

### Generating from the command line

```powershell
mtgen generate --language Punjabi --voice Child --length 2:00 --mood Upbeat
mtgen generate --language Marathi --voice Male --length 90 --seed 0xDEADBEEF
mtgen list
mtgen regenerate --id <seed-hex>        # re-render and confirm it is byte-identical
mtgen train --add "C:\my-music\hindi" --language Hindi --mood Romantic --licence "I own these"
```

Tracks and their sidecar JSON land in `%LOCALAPPDATA%\MusicTrackGenPoc\generated\`.

---

## How it works

```
Reference .wav files
   -> Analyser        tuning offset, tempo, chroma -> key/raga, per-beat chords,
                      rhythm grid, spectral balance, song form
   -> StyleProfile    tempo distribution, mode histogram, 7x7 chord transition matrix,
                      16-step rhythm probabilities, instrument weights, form template
                                            |
Seed (64-bit) --------------------------> Composer
                      scale/raga, Markov harmony with 2-bar harmonic rhythm,
                      motif melody with variation, syllable-aligned vocal line,
                      arranger that fits the requested length exactly
   -> Fingerprint     transposition-invariant hash, checked against the catalogue
   -> Renderer        per-part synthesis, formant singing voice, buses, reverb,
                      vocal-driven duck, master limiter
   -> WAV + sidecar JSON + catalogue row
```

### The two guarantees, and how they are enforced

**Every track is different.** A content fingerprint is computed from melodic *intervals*
(not absolute pitches), the melody's rhythm, the chord degrees, the arrangement and the
lyric lines. Because it is transposition-invariant, the same tune in another key is
correctly recognised as the same song. The catalogue refuses a duplicate, and generation
re-rolls the seed up to 8 times. *Verified: 200/200 distinct.*

**Every track is tuned differently.** A `TuningProfile` is sampled per track: reference
pitch (432–444 Hz), temperament (12-TET, just intonation, Pythagorean, or a 22-shruti
approximation for raga-based tracks), per-instrument micro-detune (±3–18 cents), timing and
velocity humanisation, and vibrato depth/rate/onset. It is hashed into the sidecar and
shown in the UI. *Verified: 200/200 distinct.*

**Length is exact, not approximate.** Bars are chosen for the requested duration, then the
tempo is nudged so those bars land precisely on it, so the music stays bar-aligned *and*
the file length is right. The reverb tail is folded back over the closing bars rather than
truncated. *Verified: 0 ms error at 30 / 47 / 60 / 120 / 180 s.*

### The singing voice

Source-filter synthesis: a band-limited glottal source at the melody pitch, shaped by four
parallel formant resonators tuned to the syllable's vowel, with a consonant burst at the
onset, vibrato that fades in, and micro-jitter. Voice type is a vocal-tract model — formants
scale ×1.00 / ×1.16 / ×1.32 for male / female / child, over different pitch registers.

Because it synthesises the voice rather than driving a TTS engine, **it needs no Windows
language packs, so Marathi and Punjabi sing exactly as well as Hindi and English.** The
Devanagari and Gurmukhi syllabifiers segment aksharas and read the matra to pick the vowel.

It sounds synthetic. It is on pitch, in time, and in the right language's syllable
structure — it is not a human singer, and no amount of parameter tuning would make it one.
That gap is what the optional neural-vocal phase in the design document exists to close.

---

## Verified behaviour

`mtgen selftest` — 44 checks, all passing:

| Area | Result |
|---|---|
| Syllabification, 4 scripts | Devanagari / Gurmukhi / Latin all segment; every syllable carries a vowel |
| WAV writer ↔ reader | round-trips within one 16-bit step (3.04e-5) |
| Uniqueness | 200/200 distinct fingerprints; same seed → same fingerprint |
| Tuning | 200/200 distinct tuning profiles; 6 reference pitches, 4 temperaments |
| Length accuracy | 0 ms error at 30 / 47 / 60 / 120 / 180 s (criterion: ±500 ms) |
| Reproducibility | same seed → **byte-identical** WAV; different seed → different song |
| Voice separation | male 412 Hz · female 617 Hz · child 824 Hz median F0 (7 and 5 semitones apart) |
| Four languages | all render with sung syllables at −1.8 dBFS peak |
| Analyser round-trip | tempo recovered within 2.1%; scale recovered to within one note |
| Catalogue | refuses a duplicate fingerprint |

Performance on a 4-core container, no GPU: **11.8 s for a 2-minute track** with vocals
(the design target was ≤35 s).

---

## Known limitations

- **Vocals are synthetic.** See above. This is the headline gap.
- **The analyser reads uncompressed PCM `.wav` only.** mp3/m4a need converting first; the
  error message says so rather than producing noise. Adding a decoder means taking a
  dependency, which this build deliberately avoids.
- **Key detection recovers the scale reliably but the tonic less so.** Rotationally
  equivalent modes (E Kafi, B Minor and D Major are the same seven notes) cannot be
  separated by note content alone; a bass-weighted chroma helps but does not settle it.
  A wrong root shifts the learned chord degrees consistently, which is why the transition
  matrix is blended with a prior rather than trusted outright.
- **Lyrics are assembled from a phrase bank, not written.** No language model involved.
  Paste your own on the Generate tab for real lyrics.
- **4/4 only**, one tempo per track, no key changes.
- **Instrument realism is limited** — synthesised voices with humanisation, not sampled
  performances.
- **Style transfer is statistical.** The engine learns that a corpus sits at 92 BPM and
  leans Kafi; it does not understand why a song moves.

## Before you train it on anything

The app learns from whatever you add on the Train tab, so the provenance field is
mandatory there. Use audio you own or that is openly licensed. Voice types are timbre
models (pitch and formant scaling), **not** clones of any real singer, and the child voice
is synthetic only. Exported audio is machine-generated and should not be published
commercially without review.

---

## Layout

```
MusicTrackGenPoc/
├─ MusicTrackGen.sln
├─ publish.ps1
└─ src/
   ├─ MusicTrackGen.Core/          engine — no dependencies, cross-platform
   │  ├─ Rng.cs                    seeded xoshiro256** — every random decision
   │  ├─ MusicTheory.cs            scales, ragas, chords, temperaments, TuningProfile
   │  ├─ SongScore.cs              symbolic score model + request/stage types
   │  ├─ Composer.cs               arranger, Markov harmony, motif melody, percussion
   │  ├─ Lyrics.cs                 phrase banks + Devanagari/Gurmukhi/Latin syllabifiers
   │  ├─ Dsp.cs                    biquads, Schroeder reverb, delay, compressor, limiter
   │  ├─ Instruments.cs            PolyBLEP oscillators and instrument voices
   │  ├─ Drums.cs                  kick, snare, hat, tabla, dholak by physical caricature
   │  ├─ VocalSynth.cs             IVocalSynthesizer + formant singing synthesis
   │  ├─ Renderer.cs               buses, panning, reverb, ducking, master, exact trim
   │  ├─ Fft.cs / Analysis.cs      radix-2 FFT + feature extraction
   │  ├─ Training.cs               reference library, profile builder, profile store
   │  ├─ Fingerprint.cs            transposition-invariant content hash
   │  ├─ Catalog.cs                generated-track catalogue, uniqueness enforcement
   │  ├─ TrackGenerator.cs         the end-to-end pipeline
   │  ├─ WavIo.cs                  RIFF/WAVE reader and writer
   │  └─ Workspace.cs              on-disk layout
   ├─ MusicTrackGen.Cli/           console harness + selftest (cross-platform)
   └─ MusicTrackGen.App/           WPF desktop app (Windows)
```

`MusicTrackGen.Core` and `MusicTrackGen.Cli` target `net10.0` and run anywhere, which is
how the engine is tested. Only `MusicTrackGen.App` is Windows-only.
