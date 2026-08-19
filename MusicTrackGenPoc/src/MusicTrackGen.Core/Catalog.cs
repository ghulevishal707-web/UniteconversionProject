using System.Text.Json;

namespace MusicTrackGen.Core;

/// <summary>One row of the generated-track catalogue. Everything needed to reproduce it.</summary>
public sealed class GeneratedTrackRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ulong Seed { get; set; }
    public string SeedHex => Seed.ToString("X16");
    public string StyleProfileId { get; set; } = "";
    public bool UsedBuiltInProfile { get; set; }
    public string Language { get; set; } = "";
    public string VoiceType { get; set; } = "";
    public string MoodTag { get; set; } = "";
    public double RequestedLengthSeconds { get; set; }
    public double ActualLengthSeconds { get; set; }
    public double Bpm { get; set; }
    public string KeyDescription { get; set; } = "";
    public string TuningDescription { get; set; } = "";
    public string TuningHash { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string SidecarPath { get; set; } = "";
    public int SeedAttempts { get; set; } = 1;
    public bool HadVocals { get; set; }

    public string ShortFingerprint => Core.Fingerprint.Short(Fingerprint);
}

/// <summary>
/// The generated-track catalogue, and the enforcement point for "every track is different".
/// Uniqueness is a checked invariant here, not an assumption about the PRNG.
/// </summary>
public sealed class Catalog
{
    private readonly Workspace _workspace;
    private readonly HashSet<string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    public List<GeneratedTrackRecord> Records { get; private set; } = [];

    public Catalog(Workspace workspace)
    {
        _workspace = workspace;
        Load();
    }

    public void Load()
    {
        Records = [];
        _fingerprints.Clear();
        if (!File.Exists(_workspace.CatalogFile)) return;
        try
        {
            Records = JsonSerializer.Deserialize<List<GeneratedTrackRecord>>(
                File.ReadAllText(_workspace.CatalogFile), StyleProfile.Json) ?? [];
        }
        catch (Exception)
        {
            Records = [];
        }
        foreach (var record in Records) _fingerprints.Add(record.Fingerprint);
    }

    public void Save() =>
        File.WriteAllText(_workspace.CatalogFile, JsonSerializer.Serialize(Records, StyleProfile.Json));

    public bool Contains(string fingerprint) => _fingerprints.Contains(fingerprint);

    /// <summary>Adds a record, refusing a duplicate fingerprint. This is the R5 guarantee.</summary>
    public void Add(GeneratedTrackRecord record)
    {
        if (!_fingerprints.Add(record.Fingerprint))
            throw new InvalidOperationException(
                $"Duplicate fingerprint {record.ShortFingerprint} — this song already exists in the catalogue.");
        Records.Add(record);
        Save();
    }

    public void Remove(string id)
    {
        var record = Records.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        Records.Remove(record);
        _fingerprints.Remove(record.Fingerprint);
        Save();
    }

    /// <summary>Distinct fingerprints over distinct tracks — the number the uniqueness test checks.</summary>
    public (int Tracks, int DistinctFingerprints, int DistinctTunings) Stats() =>
        (Records.Count,
         Records.Select(r => r.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
         Records.Select(r => r.TuningHash).Distinct(StringComparer.OrdinalIgnoreCase).Count());
}
