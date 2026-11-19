using System.Windows.Media.Imaging;
using UniversalSearchLauncher.Core.Models;
using UniversalSearchLauncher.Services;

namespace UniversalSearchLauncher.ViewModels;

public sealed class ResultItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isPinned;

    public ResultItemViewModel(SearchResult result, IconService iconService)
    {
        Model = result;
        _isPinned = result.IsPinned;

        if (result.Category == SearchCategory.Web)
        {
            Glyph = "\uE774";
            CategoryGlyph = "\uE774";
        }
        else if (result.IconIsDirectory)
        {
            Glyph = "\uE8B7";
            CategoryGlyph = "\uE8B7";
        }
        else
        {
            Glyph = result.Category == SearchCategory.Application ? "\uE71D" : "\uE8A5";
            CategoryGlyph = result.Category switch
            {
                SearchCategory.Application => "\uE71D",
                SearchCategory.File => result.IconIsDirectory ? "\uE8B7" : "\uE8A5",
                _ => "\uE774"
            };
        }

        Icon = result.Category == SearchCategory.Web ||
               (result.Category == SearchCategory.Application && string.IsNullOrWhiteSpace(result.IconPath))
            ? null
            : iconService.GetIcon(result.IconPath, result.IconIsDirectory, result.Extension);
    }

    public SearchResult Model { get; }
    public string Id => Model.Id;
    public string Title => Model.Title;
    public string Subtitle => Model.Subtitle;
    public string Meta => Model.Meta;
    public string DisplayMeta => Model.Category == SearchCategory.Application && Model.Meta == "Приложение"
        ? string.Empty
        : Model.Meta;
    public SearchCategory Category => Model.Category;
    public string Glyph { get; }
    public string CategoryGlyph { get; }
    public BitmapSource? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool ShowGlyph => Icon is null && Model.Category != SearchCategory.Application;
    public bool ShowLetterFallback => Icon is null && Model.Category == SearchCategory.Application;
    public string FallbackLetter => GetFallbackLetter(Title);
    public string CategoryTitle => Model.Category switch
    {
        SearchCategory.Application => "Приложения",
        SearchCategory.File => "Файлы и папки",
        _ => "В интернете"
    };

    public bool CanReveal => Model.Category != SearchCategory.Web &&
                             !string.IsNullOrWhiteSpace(Model.Target) &&
                             (File.Exists(Model.Target) || Directory.Exists(Model.Target));

    public bool CanCopy => !string.IsNullOrWhiteSpace(Model.Target) || !string.IsNullOrWhiteSpace(Model.Subtitle);
    public bool CanPin => Model.Category != SearchCategory.Web;
    public string CopyText => Model.Category == SearchCategory.Web ? "Скопировать ссылку" : "Скопировать путь";
    public string PinText => IsPinned ? "Открепить" : "Закрепить";
    public string RevealText => Model.Category == SearchCategory.Application
        ? "Открыть расположение"
        : "Показать в проводнике";

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinText));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string GroupTitle { get; set; } = string.Empty;
    public bool ShowHeader { get; set; }

    private static string GetFallbackLetter(string title)
    {
        foreach (var ch in title.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return "?";
    }
}
