using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyTextEditor.Models;

public sealed class ConditionEditorNode : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isExcluded;
    private bool _matchAll = true;
    public bool IsGroup { get; init; }
    public ObservableCollection<ConditionEditorNode> Children { get; } = [];
    public string Text { get => _text; set { _text = value; OnPropertyChanged(); } }
    public bool IsExcluded { get => _isExcluded; set { _isExcluded = value; OnPropertyChanged(); } }
    public bool MatchAll { get => _matchAll; set { _matchAll = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SearchResultRow(int LineNumber, string Text, bool IsContext = false);
public sealed record TransformPreviewRow(int LineNumber, string Before, string After, string Status);
