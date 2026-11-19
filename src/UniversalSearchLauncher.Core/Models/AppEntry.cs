using System.Text;
using System.Text.Json.Serialization;

namespace UniversalSearchLauncher.Core.Models;

/// <summary>
/// An application discovered in the Windows Start Menu.
/// </summary>
public sealed class AppEntry
{
    public string Name { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string? ShortcutPath { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Aumid { get; set; }
    public string? IconPath { get; set; }
    public string? Keywords { get; set; }
    public string Source { get; set; } = "Start Menu";

    private string? _searchText;
    private string? _identity;

    [JsonIgnore]
    public string Identity
    {
        get
        {
            if (_identity is not null)
            {
                return _identity;
            }

            _identity = !string.IsNullOrWhiteSpace(Aumid)
                ? "aumid:" + Aumid
                : !string.IsNullOrWhiteSpace(ShortcutPath)
                    ? "lnk:" + ShortcutPath!.ToLowerInvariant()
                    : !string.IsNullOrWhiteSpace(ExecutablePath)
                        ? "exe:" + ExecutablePath!.ToLowerInvariant()
                        : "name:" + Name.ToLowerInvariant();

            return _identity;
        }
    }

    [JsonIgnore]
    public LaunchMode Mode => !string.IsNullOrWhiteSpace(Aumid)
        ? LaunchMode.Aumid
        : !string.IsNullOrWhiteSpace(ShortcutPath)
            ? LaunchMode.Shortcut
            : LaunchMode.Executable;

    [JsonIgnore]
    public string? LaunchTarget => Mode switch
    {
        LaunchMode.Aumid => Aumid,
        LaunchMode.Shortcut => ShortcutPath,
        _ => ExecutablePath
    };

    [JsonIgnore]
    public string? BestIconPath => !string.IsNullOrWhiteSpace(IconPath) && File.Exists(IconPath)
        ? IconPath
        : !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath)
            ? ExecutablePath
            : ShortcutPath;

    /// <summary>
    /// Normalised text used by the ranker. Built once when the application index is created.
    /// </summary>
    [JsonIgnore]
    public string SearchText
    {
        get
        {
            if (_searchText is not null)
            {
                return _searchText;
            }

            var builder = new StringBuilder(Name.Length + 96);
            builder.Append(Name);

            if (!string.IsNullOrWhiteSpace(ExecutablePath))
            {
                builder.Append(' ').Append(Path.GetFileNameWithoutExtension(ExecutablePath));
            }

            if (!string.IsNullOrWhiteSpace(ShortcutPath))
            {
                builder.Append(' ').Append(Path.GetFileNameWithoutExtension(ShortcutPath));
            }

            if (!string.IsNullOrWhiteSpace(Keywords))
            {
                builder.Append(' ').Append(Keywords);
            }

            _searchText = Core.Search.FuzzyMatcher.NormalizeForSearch(builder.ToString());
            return _searchText;
        }
    }
}
