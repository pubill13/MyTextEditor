using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Controls;

namespace MyTextEditor.Models;

public sealed class DocumentViewModel : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isModified;

    public string? FilePath { get; set; }
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public bool HasByteOrderMark { get; set; }
    public string NewLine { get; set; } = "\r\n";
    public TextBox? Editor { get; set; }
    public string DisplayName => FilePath is null ? "새 문서" : Path.GetFileName(FilePath);
    public string TabTitle => IsModified ? $"{DisplayName} •" : DisplayName;

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; IsModified = true; OnPropertyChanged(); }
    }

    public bool IsModified
    {
        get => _isModified;
        set { if (_isModified == value) return; _isModified = value; OnPropertyChanged(); OnPropertyChanged(nameof(TabTitle)); }
    }

    public void NotifyIdentityChanged() { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(TabTitle)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
