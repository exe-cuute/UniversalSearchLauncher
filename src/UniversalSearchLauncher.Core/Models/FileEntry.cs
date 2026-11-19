using UniversalSearchLauncher.Core.Search;

namespace UniversalSearchLauncher.Core.Models;

/// <summary>
/// A file-system item kept in the in-memory search index.
/// </summary>
public sealed class FileEntry
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string ParentName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    private string? _searchText;

    /// <summary>
    /// Name + parent folder + extension, normalised once so each keystroke only has to score it.
    /// </summary>
    public string SearchText
    {
        get
        {
            if (_searchText is null)
            {
                _searchText = FuzzyMatcher.NormalizeForSearch(
                    Name + " " + ParentName + " " + Extension.TrimStart('.'));
            }

            return _searchText;
        }
        set => _searchText = value;
    }

    public FileEntry CloneForCache()
    {
        return new FileEntry
        {
            FullPath = FullPath,
            Name = Name,
            DirectoryPath = DirectoryPath,
            ParentName = ParentName,
            Extension = Extension,
            IsDirectory = IsDirectory,
            Size = Size,
            ModifiedUtc = ModifiedUtc
        };
    }
}
