using System.Text.Json;
using UnifiedRgb.Core.Models;

namespace UnifiedRgb.Core.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _directory;

    public ProfileStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnifiedRgb",
            "profiles");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public IReadOnlyList<string> ListProfiles()
    {
        return Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();
    }

    public void Save(ColorProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));

        var safe = SanitizeFileName(profile.Name);
        profile.SavedAt = DateTimeOffset.Now;
        var path = Path.Combine(_directory, safe + ".json");
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
    }

    public ColorProfile? Load(string name)
    {
        var safe = SanitizeFileName(name);
        var path = Path.Combine(_directory, safe + ".json");
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ColorProfile>(json, JsonOptions);
    }

    public bool Delete(string name)
    {
        var safe = SanitizeFileName(name);
        var path = Path.Combine(_directory, safe + ".json");
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "profile" : cleaned;
    }
}
