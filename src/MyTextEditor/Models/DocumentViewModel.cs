using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using MyTextEditor.Controls;
using MyTextEditor.Core;

namespace MyTextEditor.Models;

public sealed class DocumentViewModel : INotifyPropertyChanged
{
    private bool _isModified;
    private bool _encodingModified;
    private DocumentEncodingKind _savedEncoding = DocumentEncodingKind.Utf8;
    public bool EncodingModified
    {
        get => _encodingModified;
        private set { if (_encodingModified == value) return; _encodingModified = value; OnPropertyChanged(nameof(IsModified)); OnPropertyChanged(nameof(TabTitle)); }
    }

    public void ChangeEncoding(DocumentEncodingKind kind)
    {
        Encoding = DocumentEncodings.GetEncoding(kind);
        HasByteOrderMark = DocumentEncodings.HasBom(kind);
        EncodingModified = kind != _savedEncoding;
    }

    public void AcceptEncodingChanges()
    {
        _savedEncoding = DocumentEncodings.FromEncoding(Encoding, HasByteOrderMark);
        EncodingModified = false;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string? FilePath { get; set; }
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public bool HasByteOrderMark { get; set; }
    public string NewLine { get; set; } = "\r\n";
    public ScintillaEditorHost Editor { get; set; } = null!;
    public string DisplayName => FilePath is null ? "새 문서" : Path.GetFileName(FilePath);
    public string TabTitle => IsModified ? $"{DisplayName} •" : DisplayName;

    public string Text => Editor.GetText();
    public long ContentRevision => Editor.ContentRevision;

    public bool IsModified
    {
        get => _isModified || _encodingModified;
        set { if (_isModified == value) return; _isModified = value; OnPropertyChanged(); OnPropertyChanged(nameof(TabTitle)); }
    }

    public void NotifyIdentityChanged() { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(TabTitle)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
