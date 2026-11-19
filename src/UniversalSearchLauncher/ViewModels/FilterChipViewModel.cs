using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.ViewModels;

public sealed class FilterChipViewModel : ObservableObject
{
    private bool _isSelected;
    private int _count;

    public FilterChipViewModel(SearchCategory? category, string label)
    {
        Category = category;
        Label = label;
    }

    public SearchCategory? Category { get; }
    public string Label { get; }
    public bool IsAll => Category is null;
    public string DisplayLabel => Count > 0 ? $"{Label}  {Count}" : Label;
    public bool IsVisible => IsAll || Count > 0 || IsSelected;

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }
}
