using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MyTextEditor.Core;
using MyTextEditor.Controls;
using MyTextEditor.Models;
using WpfClipboard = System.Windows.Clipboard;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace MyTextEditor.Diff;

public partial class DiffSourceDialog : Window
{
    private readonly IReadOnlyList<DocumentViewModel> _documents;
    private readonly DocumentViewModel? _current;
    private readonly ScintillaEditorHost? _currentEditor;
    private readonly DocumentFileService _fileService = new();

    public DiffSourceDialog(IEnumerable<DocumentViewModel> documents, DocumentViewModel? current,
        ScintillaEditorHost? currentEditor, DiffWindowOptions defaults)
    {
        InitializeComponent();
        _documents = documents.ToArray();
        _current = current;
        _currentEditor = currentEditor;
        LeftDocumentCombo.ItemsSource = _documents;
        RightDocumentCombo.ItemsSource = _documents;
        LeftDocumentCombo.SelectedItem = current ?? _documents.FirstOrDefault();
        RightDocumentCombo.SelectedItem = _documents.FirstOrDefault(item => !ReferenceEquals(item, current)) ?? current;
        LeftKindCombo.SelectedIndex = current is null ? 1 : 0;
        RightKindCombo.SelectedIndex = _documents.Count > 1 ? 0 : 1;
        Loaded += (_, _) =>
        {
            RefreshSide(true);
            RefreshSide(false);
        };
        Dispatcher.BeginInvoke(() => { RefreshSide(true); RefreshSide(false); });
    }

    public DiffEndpoint? LeftEndpoint { get; private set; }
    public DiffEndpoint? RightEndpoint { get; private set; }

    private void SourceKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshSide(true);
        RefreshSide(false);
    }

    private void RefreshSide(bool left)
    {
        var kind = SelectedKind(left ? LeftKindCombo : RightKindCombo);
        var documents = left ? LeftDocumentCombo : RightDocumentCombo;
        var filePanel = left ? LeftFilePanel : RightFilePanel;
        var hint = left ? LeftHint : RightHint;
        documents.Visibility = kind == DiffEndpointKind.OpenDocument ? Visibility.Visible : Visibility.Collapsed;
        filePanel.Visibility = kind == DiffEndpointKind.File ? Visibility.Visible : Visibility.Collapsed;
        hint.Text = kind switch
        {
            DiffEndpointKind.Clipboard => "현재 클립보드의 텍스트를 읽기 전용으로 비교합니다.",
            DiffEndpointKind.Selection => _currentEditor?.HasSelection == true ? "현재 문서의 선택 영역을 읽기 전용으로 비교합니다." : "현재 문서에 선택된 텍스트가 없습니다.",
            _ => string.Empty
        };
    }

    private static DiffEndpointKind SelectedKind(WpfComboBox combo) =>
        Enum.TryParse<DiffEndpointKind>((combo.SelectedItem as WpfComboBoxItem)?.Tag?.ToString(), out var kind) ? kind : DiffEndpointKind.OpenDocument;

    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftFileText);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightFileText);

    private static void Browse(WpfTextBox target)
    {
        var dialog = new WpfOpenFileDialog { Filter = "텍스트 및 로그 파일|*.txt;*.log;*.csv;*.json;*.xml;*.md;*.ini;*.cfg|모든 파일|*.*" };
        if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        try
        {
            IsEnabled = false;
            LeftEndpoint = await CreateEndpointAsync(true);
            RightEndpoint = await CreateEndpointAsync(false);
            if (LeftEndpoint.Kind == DiffEndpointKind.OpenDocument && RightEndpoint.Kind == DiffEndpointKind.OpenDocument &&
                LeftEndpoint.SourceDocumentId == RightEndpoint.SourceDocumentId)
                throw new InvalidOperationException("서로 다른 문서나 소스를 선택하세요.");
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            IsEnabled = true;
        }
    }

    private async Task<DiffEndpoint> CreateEndpointAsync(bool left)
    {
        var kind = SelectedKind(left ? LeftKindCombo : RightKindCombo);
        if (kind == DiffEndpointKind.OpenDocument)
        {
            var document = (left ? LeftDocumentCombo : RightDocumentCombo).SelectedItem as DocumentViewModel
                ?? throw new InvalidOperationException("비교할 열린 문서를 선택하세요.");
            return new DiffEndpoint
            {
                Kind = kind, DisplayName = document.DisplayName, Text = document.Text, FilePath = document.FilePath,
                Encoding = document.Encoding, HasByteOrderMark = document.HasByteOrderMark, NewLine = document.NewLine,
                SourceDocumentId = document.Id, SourceRevision = document.ContentRevision
            };
        }
        if (kind == DiffEndpointKind.File)
        {
            var path = (left ? LeftFileText : RightFileText).Text;
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("비교할 파일을 선택하세요.");
            var state = await _fileService.LoadAsync(path);
            var info = new FileInfo(path);
            return new DiffEndpoint
            {
                Kind = kind, DisplayName = info.Name, Text = state.Text, FilePath = info.FullName,
                Encoding = state.Encoding, HasByteOrderMark = state.HasByteOrderMark, NewLine = state.NewLine,
                SourceFileLength = info.Length, SourceFileLastWriteUtc = info.LastWriteTimeUtc
            };
        }
        if (kind == DiffEndpointKind.Clipboard)
        {
            if (!WpfClipboard.ContainsText()) throw new InvalidOperationException("클립보드에 비교할 텍스트가 없습니다.");
            return new DiffEndpoint { Kind = kind, DisplayName = "클립보드", Text = WpfClipboard.GetText(), IsReadOnly = true };
        }
        if (_current is null || _currentEditor?.HasSelection != true)
            throw new InvalidOperationException("현재 문서에서 비교할 텍스트를 먼저 선택하세요.");
        return new DiffEndpoint
        {
            Kind = kind, DisplayName = $"{_current.DisplayName} 선택 영역", Text = _currentEditor.SelectedText,
            NewLine = _current.NewLine, IsReadOnly = true
        };
    }
}
