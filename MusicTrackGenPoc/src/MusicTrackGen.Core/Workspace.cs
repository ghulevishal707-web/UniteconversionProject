namespace MusicTrackGen.Core;

/// <summary>
/// On-disk layout for the app's data. Plain JSON plus WAV files instead of a database
/// engine, so the PoC has nothing to install and everything is inspectable by hand.
/// </summary>
public sealed class Workspace
{
    public string Root { get; }

    public Workspace(string? root = null)
    {
        Root = root ?? DefaultRoot();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(GeneratedDirectory);
    }

    public static string DefaultRoot()
    {
        // On Windows this lands in %LOCALAPPDATA%; elsewhere in ~/.local/share.
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "MusicTrackGenPoc");
    }

    public string LibraryFile => Path.Combine(Root, "library.json");
    public string CatalogFile => Path.Combine(Root, "catalog.json");
    public string ProfilesDirectory => Path.Combine(Root, "profiles");
    public string GeneratedDirectory => Path.Combine(Root, "generated");

    public string ProfilePath(string profileId) =>
        Path.Combine(ProfilesDirectory, SafeFileName(profileId) + ".json");

    public static string SafeFileName(string value)
    {
        var chars = value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
        return new string(chars.ToArray());
    }
}
