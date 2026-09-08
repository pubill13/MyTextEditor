using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Models;
using MyTextEditor.Services;

namespace MyTextEditor;

public partial class MainWindow : Window
{
    private readonly DocumentFileService _fileService = new();
    private readonly TextSearchEngine _searchEngine = new();
    private readonly TextTransformService _transformService = new();
    private readonly UserSettings _settings;
    private readonly ConditionEditorNode _rootCondition = new() { IsGroup = true, MatchAll = true };
    private IReadOnlyList<int> _matchedLineNumbers = [];
    private string? _pendingTransformedText;
    private DocumentViewModel? _resultDocument;
    private string? _resultSourceText;
    private bool _resultInvalidated;
    private bool _allowClose;
    private bool _closingInProgress;
    private bool _advancedMode;
    private ConditionEditorNode? _conditionToFocus;
    private readonly List<TransformPreviewRow> _allPreviewRows = [];
    private readonly string _settingsSnapshot;

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];
    public ObservableCollection<SearchResultRow> SearchResults { get; } = [];
    public ObservableCollection<TransformPreviewRow> PreviewRows { get; } = [];

    private DocumentViewModel? CurrentDocument => DocumentTabs.SelectedItem as DocumentViewModel;
    private TextBox? CurrentEditor => CurrentDocument?.Editor;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _settings = SettingsService.Load();
        _settingsSnapshot = JsonSerializer.Serialize(_settings);
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        RestoreWindowPlacement();
        ToolColumn.Width = new GridLength(Math.Max(280, _settings.ToolPanelWidth));
        ResultRow.Height = new GridLength(Math.Max(120, _settings.ResultPanelHeight));
        Application.Current.Resources["EditorFontFamily"] = new FontFamily(_settings.EditorFontFamily);
        Application.Current.Resources["EditorFontSize"] = _settings.EditorFontSize;
        ApplyTheme(_settings.Theme);
        LoadInstalledFonts();
        SelectComboItem(FontFamilyCombo, _settings.EditorFontFamily);
        SelectComboItem(OverflowFontFamilyCombo, _settings.EditorFontFamily);
        SelectComboItem(FontSizeCombo, _settings.EditorFontSize.ToString("0"));
        SelectComboItem(OverflowFontSizeCombo, _settings.EditorFontSize.ToString("0"));
        RestorePanelVisibility();
        BuildRecentFilesMenu();

        _rootCondition.Children.Add(new ConditionEditorNode());
        RenderAdvancedConditions();
        UpdateConditionSummary();
        NewDocument();
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewDocument();

    private void NewDocument(string text = "", string? title = null)
    {
        var document = new DocumentViewModel { Text = text, IsModified = text.Length > 0 };
        if (!string.IsNullOrWhiteSpace(title)) document.FilePath = title;
        document.Editor = CreateEditor(document);
        Documents.Add(document);
        DocumentTabs.SelectedItem = document;
        EmptyDocumentState.Visibility = Visibility.Collapsed;
        StatusMessage.Text = "새 문서를 만들었습니다.";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "텍스트 파일 열기",
            Filter = "텍스트 파일|*.txt;*.log;*.csv;*.md;*.json;*.xml;*.yaml;*.yml|모든 파일|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) await OpenFileAsync(file);
    }

    private async Task OpenFileAsync(string filePath)
    {
        try
        {
            var alreadyOpen = Documents.FirstOrDefault(item => string.Equals(item.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
            if (alreadyOpen is not null) { DocumentTabs.SelectedItem = alreadyOpen; return; }

            var state = await _fileService.LoadAsync(filePath);
            var document = new DocumentViewModel
            {
                FilePath = state.FilePath,
                Text = state.Text,
                Encoding = state.Encoding,
                HasByteOrderMark = state.HasByteOrderMark,
                NewLine = state.NewLine,
                IsModified = false
            };
            document.Editor = CreateEditor(document);
            Documents.Add(document);
            DocumentTabs.SelectedItem = document;
            AddRecentFile(filePath);
            StatusMessage.Text = $"{document.DisplayName} 파일을 열었습니다.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            MessageBox.Show(this, $"파일을 열 수 없습니다.\n\n{exception.Message}", "파일 열기", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveDocumentAsync(CurrentDocument, false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveDocumentAsync(CurrentDocument, true);

    private async Task<bool> SaveDocumentAsync(DocumentViewModel? document, bool saveAs)
    {
        if (document is null) return true;
        var path = document.FilePath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Title = "다른 이름으로 저장",
                FileName = document.FilePath is null ? "새 문서.txt" : document.DisplayName,
                Filter = "텍스트 파일|*.txt|로그 파일|*.log|마크다운|*.md|모든 파일|*.*"
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        var state = ToCoreDocument(document);
        try
        {
            await _fileService.SaveAsync(state, path);
        }
        catch (DocumentEncodingException exception)
        {
            var answer = MessageBox.Show(this, $"{exception.Message}\n\nUTF-8로 저장할까요?", "문자 인코딩", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;
            state.Encoding = new UTF8Encoding(false, true);
            state.HasByteOrderMark = false;
            try
            {
                await _fileService.SaveAsync(state, path);
            }
            catch (Exception retryException) when (retryException is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"UTF-8로도 파일을 저장할 수 없습니다.\n\n{retryException.Message}", "저장", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"파일을 저장할 수 없습니다.\n\n{exception.Message}", "저장", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        document.FilePath = state.FilePath;
        document.Encoding = state.Encoding;
        document.HasByteOrderMark = state.HasByteOrderMark;
        document.IsModified = false;
        document.NotifyIdentityChanged();
        AddRecentFile(path!);
        UpdateStatus();
        StatusMessage.Text = $"{document.DisplayName} 파일을 저장했습니다.";
        return true;
    }

    private static DocumentState ToCoreDocument(DocumentViewModel document) => new()
    {
        FilePath = document.FilePath,
        Text = document.Text,
        IsModified = document.IsModified,
        Encoding = document.Encoding,
        HasByteOrderMark = document.HasByteOrderMark,
        NewLine = document.NewLine
    };

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DocumentViewModel document) await CloseDocumentAsync(document);
        e.Handled = true;
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is not null) await CloseDocumentAsync(CurrentDocument);
    }

    private async Task<bool> CloseDocumentAsync(DocumentViewModel document)
    {
        if (document.IsModified)
        {
            var answer = MessageBox.Show(this, $"'{document.DisplayName}'의 변경 내용을 저장할까요?", "문서 닫기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !await SaveDocumentAsync(document, false)) return false;
        }
        Documents.Remove(document);
        EmptyDocumentState.Visibility = Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
        return true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { if (CurrentEditor?.CanUndo == true) CurrentEditor.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (CurrentEditor?.CanRedo == true) CurrentEditor.Redo(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => CurrentEditor?.SelectAll();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (!Documents.Any(document => document.IsModified))
        {
            SaveSettings();
            return;
        }
        e.Cancel = true;
        if (_closingInProgress) return;
        _closingInProgress = true;
        if (!await ConfirmCloseAllAsync())
        {
            _closingInProgress = false;
            return;
        }

        SaveSettings();
        _allowClose = true;
        Close();
    }

    private async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var document in Documents.Where(item => item.IsModified).ToArray())
        {
            DocumentTabs.SelectedItem = document;
            var answer = MessageBox.Show(this, $"'{document.DisplayName}'의 변경 내용을 저장할까요?", "프로그램 종료", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !await SaveDocumentAsync(document, false)) return false;
        }
        return true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.N) { NewDocument(); e.Handled = true; }
        else if (e.Key == Key.O) { Open_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) { SaveAs_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.S) { Save_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.W) { CloseTab_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F) { ToolTabs.SelectedIndex = 0; (_advancedMode ? (FrameworkElement)AdvancedConditionsHost : SimpleAllBox).Focus(); e.Handled = true; }
        else if (e.Key == Key.F4) { MoveToSearchResult((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; }
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateStatus();
    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox editor && editor.DataContext is DocumentViewModel document && document == _resultDocument && editor.Text != _resultSourceText)
            InvalidateResults("원문이 변경되었습니다. 검색 또는 미리보기를 다시 실행하세요.");
        UpdateStatus();
    }

    private void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != DocumentTabs) return;
        if (_resultDocument is not null && CurrentDocument != _resultDocument)
            InvalidateResults("문서 탭이 변경되었습니다. 현재 문서에서 검색 또는 미리보기를 다시 실행하세요.");
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var document = CurrentDocument;
        var editor = CurrentEditor;
        if (document is null)
        {
            CaretStatus.Text = "줄 -, 열 -"; LineCountStatus.Text = "0줄"; EncodingStatus.Text = "-"; NewLineStatus.Text = "-"; return;
        }
        var caret = editor?.CaretIndex ?? 0;
        var line = Math.Max(0, editor?.GetLineIndexFromCharacterIndex(caret) ?? 0);
        var lineStart = Math.Max(0, editor?.GetCharacterIndexFromLineIndex(line) ?? 0);
        CaretStatus.Text = $"줄 {line + 1}, 열 {caret - lineStart + 1}";
        LineCountStatus.Text = $"{CountLines(document.Text):N0}줄";
        EncodingStatus.Text = document.Encoding.WebName.ToUpperInvariant();
        NewLineStatus.Text = document.NewLine == "\r\n" ? "CRLF" : document.NewLine == "\n" ? "LF" : "CR";
    }

    private TextBox CreateEditor(DocumentViewModel document)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(22, 18, 22, 18),
            UndoLimit = 200,
            SpellCheck = { IsEnabled = false }
        };
        editor.SetResourceReference(Control.FontFamilyProperty, "EditorFontFamily");
        editor.SetResourceReference(Control.FontSizeProperty, "EditorFontSize");
        editor.SetBinding(TextBox.TextProperty, new Binding(nameof(DocumentViewModel.Text))
        {
            Source = document,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        editor.DataContext = document;
        editor.SelectionChanged += Editor_SelectionChanged;
        editor.TextChanged += Editor_TextChanged;
        return editor;
    }

    private void SimpleMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_advancedMode) return;
        var hasAdvancedInput = EnumerateConditions(_rootCondition).Any(item => !string.IsNullOrWhiteSpace(item.Text));
        if (hasAdvancedInput && MessageBox.Show(this, "고급 조건을 초기화하고 간편 검색으로 전환할까요?", "검색 방식 변경", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _rootCondition.Children.Clear();
        _rootCondition.MatchAll = true;
        _rootCondition.Children.Add(new ConditionEditorNode());
        SimpleAllBox.Clear();
        SimpleAnyBox.Clear();
        SimpleExcludeBox.Clear();
        _advancedMode = false;
        AdvancedConditionsPanel.Visibility = Visibility.Collapsed;
        SimpleConditionsPanel.Visibility = Visibility.Visible;
        RenderAdvancedConditions();
        UpdateConditionSummary();
    }

    private void AdvancedMode_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedMode) return;
        var simpleCondition = BuildSimpleCondition();
        _rootCondition.Children.Clear();
        _rootCondition.MatchAll = true;
        if (simpleCondition is ConditionGroup simpleGroup)
            foreach (var child in simpleGroup.Children) AddCoreConditionToEditor(_rootCondition, child);
        if (_rootCondition.Children.Count == 0) _rootCondition.Children.Add(new ConditionEditorNode());
        _advancedMode = true;
        SimpleConditionsPanel.Visibility = Visibility.Collapsed;
        AdvancedConditionsPanel.Visibility = Visibility.Visible;
        RenderAdvancedConditions();
        UpdateConditionSummary();
    }

    private void RenderAdvancedConditions()
    {
        AdvancedConditionsHost.Children.Clear();
        AdvancedConditionsHost.Children.Add(CreateGroupCard(_rootCondition, null, true));
    }

    private Border CreateGroupCard(ConditionEditorNode group, ConditionEditorNode? parent, bool isRoot = false)
    {
        var body = new StackPanel();
        var header = new WrapPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = isRoot ? "전체 조건" : "괄호로 묶인 조건",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 7, 0),
            ToolTip = isRoot ? "검색식 전체를 결합하는 기준입니다." : "이 카드 안 조건은 괄호 하나처럼 먼저 계산됩니다."
        });
        var operation = new ComboBox { Width = 128, Height = 32, SelectedIndex = group.MatchAll ? 0 : 1, ToolTip = "그룹 안 조건을 결합하는 방식" };
        operation.Items.Add("모두 만족 (AND)"); operation.Items.Add("하나라도 (OR)");
        operation.SelectionChanged += (_, _) => { group.MatchAll = operation.SelectedIndex == 0; UpdateConditionSummary(); };
        header.Children.Add(operation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ActionButton("＋ 조건", "이 그룹에 조건 추가", () =>
        {
            var condition = new ConditionEditorNode();
            group.Children.Add(condition);
            _conditionToFocus = condition;
            RenderAdvancedConditions();
            UpdateConditionSummary();
        }));
        actions.Children.Add(ActionButton("＋ 그룹", "이 그룹에 하위 그룹 추가", () =>
        {
            var child = new ConditionEditorNode { IsGroup = true };
            var condition = new ConditionEditorNode();
            child.Children.Add(condition);
            group.Children.Add(child);
            _conditionToFocus = condition;
            RenderAdvancedConditions();
            UpdateConditionSummary();
        }));
        if (!isRoot && parent is not null) actions.Children.Add(ActionButton("삭제", "이 그룹 삭제", () => { parent.Children.Remove(group); RenderAdvancedConditions(); UpdateConditionSummary(); }));
        header.Children.Add(actions); body.Children.Add(header);

        foreach (var child in group.Children)
        {
            if (child.IsGroup) body.Children.Add(CreateGroupCard(child, group));
            else body.Children.Add(CreateConditionRow(child, group));
        }
        var card = new Border { Child = body, MinWidth = 280, Margin = isRoot ? new Thickness(0) : new Thickness(12, 7, 0, 0), Padding = new Thickness(8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        return card;
    }

    private FrameworkElement CreateConditionRow(ConditionEditorNode condition, ConditionEditorNode parent)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var kind = new ComboBox { Height = 36, SelectedIndex = condition.IsExcluded ? 1 : 0 };
        kind.Items.Add("포함함"); kind.Items.Add("포함 안 함");
        kind.SelectionChanged += (_, _) => { condition.IsExcluded = kind.SelectedIndex == 1; UpdateConditionSummary(); };
        var input = new TextBox { MinHeight = 36, Margin = new Thickness(5, 0, 5, 0), Text = condition.Text };
        input.TextChanged += (_, _) => { condition.Text = input.Text; UpdateConditionSummary(); };
        input.KeyDown += SearchInput_KeyDown;
        if (ReferenceEquals(_conditionToFocus, condition))
        {
            _conditionToFocus = null;
            input.Loaded += (_, _) => { input.Focus(); input.CaretIndex = input.Text.Length; };
        }
        var remove = ActionButton("×", "이 조건 삭제", () => { parent.Children.Remove(condition); RenderAdvancedConditions(); UpdateConditionSummary(); });
        Grid.SetColumn(kind, 0); Grid.SetColumn(input, 1); Grid.SetColumn(remove, 2);
        grid.Children.Add(kind); grid.Children.Add(input); grid.Children.Add(remove);
        var row = new Border { Child = grid, MinHeight = 36, Background = Brushes.Transparent, CornerRadius = new CornerRadius(4) };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, args) => { if (args.OriginalSource is Border or Grid) input.Focus(); };
        return row;
    }

    private static Button ActionButton(string text, string tooltip, Action action)
    {
        var button = new Button { Content = text, ToolTip = tooltip, MinWidth = 36, MinHeight = 36, Padding = new Thickness(7, 4, 7, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    private static IEnumerable<ConditionEditorNode> EnumerateConditions(ConditionEditorNode group) => group.Children.SelectMany(child => child.IsGroup ? EnumerateConditions(child) : [child]);

    private void SimpleCondition_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderSimpleTags();
        UpdateConditionSummary();
    }
    private void SearchInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && SearchButton.IsEnabled) { Search_Click(sender, new RoutedEventArgs()); e.Handled = true; } }

    private void UpdateConditionSummary()
    {
        var valid = _advancedMode
            ? EnumerateConditions(_rootCondition).Any(item => !string.IsNullOrWhiteSpace(item.Text))
            : SimpleDescriptions().Length > 0;
        ConditionSummaryText.Text = !valid
            ? "검색 조건을 입력하세요."
            : _advancedMode
                ? $"{DescribeAdvancedGroup(_rootCondition)}인 줄을 찾습니다."
                : $"{string.Join(", ", SimpleDescriptions())}인 줄을 찾습니다.";
        SearchButton.IsEnabled = valid;
    }

    private static string DescribeAdvancedGroup(ConditionEditorNode group, bool nested = false)
    {
        var parts = group.Children
            .Select(child => child.IsGroup
                ? DescribeAdvancedGroup(child, true)
                : string.IsNullOrWhiteSpace(child.Text)
                    ? string.Empty
                    : child.IsExcluded ? $"'{child.Text}' 제외" : $"'{child.Text}' 포함")
            .Where(part => part.Length > 0)
            .ToArray();
        if (parts.Length == 0) return "고급 조건 사용 중";
        var description = string.Join(group.MatchAll ? " 그리고 " : " 또는 ", parts);
        return nested && parts.Length > 1 ? $"({description})" : description;
    }

    private string[] SimpleDescriptions()
    {
        var all = ParseTerms(SimpleAllBox.Text).Select(value => $"'{value}' 모두 포함");
        var any = ParseTerms(SimpleAnyBox.Text).Select(value => $"'{value}' 중 하나 포함");
        var excluded = ParseTerms(SimpleExcludeBox.Text).Select(value => $"'{value}' 제외");
        return all.Concat(any).Concat(excluded).ToArray();
    }

    private static string[] ParseTerms(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(value => value.Length > 0).ToArray();

    private void RenderSimpleTags()
    {
        if (SimpleAllTags is null || SimpleAnyTags is null || SimpleExcludeTags is null) return;
        RenderTagList(SimpleAllTags, SimpleAllBox);
        RenderTagList(SimpleAnyTags, SimpleAnyBox);
        RenderTagList(SimpleExcludeTags, SimpleExcludeBox);
    }

    private void RenderTagList(Panel host, TextBox source)
    {
        host.Children.Clear();
        foreach (var term in ParseTerms(source.Text))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = term, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "×", ToolTip = $"'{term}' 조건 삭제", MinWidth = 24, MinHeight = 24, Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(5, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                var terms = ParseTerms(source.Text).ToList();
                var index = terms.FindIndex(value => string.Equals(value, term, StringComparison.Ordinal));
                if (index >= 0) terms.RemoveAt(index);
                source.Text = string.Join(", ", terms);
                source.CaretIndex = source.Text.Length;
                source.Focus();
            };
            content.Children.Add(remove);
            var chip = new Border { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 2, 3, 2), Margin = new Thickness(0, 0, 5, 4) };
            chip.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            chip.BorderThickness = new Thickness(1);
            host.Children.Add(chip);
        }
    }

    private ConditionNode BuildSimpleCondition()
    {
        var children = new List<ConditionNode>();
        children.AddRange(ParseTerms(SimpleAllBox.Text).Select(value => new TextCondition(TextConditionKind.Contains, value)));
        var any = ParseTerms(SimpleAnyBox.Text).Select(value => (ConditionNode)new TextCondition(TextConditionKind.Contains, value)).ToArray();
        if (any.Length > 0) children.Add(new ConditionGroup(ConditionOperator.Any, any));
        children.AddRange(ParseTerms(SimpleExcludeBox.Text).Select(value => new TextCondition(TextConditionKind.DoesNotContain, value)));
        return new ConditionGroup(ConditionOperator.All, children);
    }

    private static void AddCoreConditionToEditor(ConditionEditorNode parent, ConditionNode condition)
    {
        if (condition is TextCondition text) parent.Children.Add(new ConditionEditorNode { Text = text.Value, IsExcluded = text.Kind == TextConditionKind.DoesNotContain });
        else if (condition is ConditionGroup group)
        {
            var editorGroup = new ConditionEditorNode { IsGroup = true, MatchAll = group.Operator == ConditionOperator.All };
            foreach (var child in group.Children) AddCoreConditionToEditor(editorGroup, child);
            parent.Children.Add(editorGroup);
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null) return;
        try
        {
            var condition = _advancedMode ? ToCoreCondition(_rootCondition) : BuildSimpleCondition();
            var options = new SearchOptions(MatchCaseCheck.IsChecked == true, WholeWordCheck.IsChecked == true, SelectedNumber(ContextLinesCombo));
            var results = _searchEngine.Search(CurrentDocument.Text, condition, options);
            SearchResults.Clear();
            var matchedLines = results.Select(result => result.LineNumber).ToHashSet();
            var displayLines = new Dictionary<int, string>();
            foreach (var result in results)
            {
                foreach (var context in result.Context)
                    displayLines.TryAdd(context.LineNumber, context.Text);
            }
            foreach (var line in displayLines.OrderBy(item => item.Key))
                SearchResults.Add(new SearchResultRow(line.Key, line.Value, !matchedLines.Contains(line.Key)));
            _matchedLineNumbers = results.Select(result => result.LineNumber).ToArray();
            CaptureResultSource();
            ShowSearchResults();
            ResultHeader.Text = $"검색 결과  ·  {results.Count:N0}개 일치";
            StatusMessage.Text = results.Count == 0 ? "일치하는 줄이 없습니다." : $"{results.Count:N0}개 줄을 찾았습니다.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "조건 검색", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static ConditionNode ToCoreCondition(ConditionEditorNode node)
    {
        if (!node.IsGroup) return new TextCondition(node.IsExcluded ? TextConditionKind.DoesNotContain : TextConditionKind.Contains, node.Text);
        return new ConditionGroup(node.MatchAll ? ConditionOperator.All : ConditionOperator.Any, node.Children.Select(ToCoreCondition).ToArray());
    }

    private void SearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is SearchResultRow row) GoToLine(row.LineNumber);
    }

    private void MoveToSearchResult(int direction)
    {
        if (SearchResults.Count == 0) return;
        var index = SearchResultsList.SelectedIndex;
        index = index < 0 ? 0 : (index + direction + SearchResults.Count) % SearchResults.Count;
        SearchResultsList.SelectedIndex = index;
        SearchResultsList.ScrollIntoView(SearchResults[index]);
    }

    private void GoToLine(int lineNumber)
    {
        var editor = CurrentEditor;
        if (editor is null || lineNumber < 1 || lineNumber > editor.LineCount) return;
        var start = editor.GetCharacterIndexFromLineIndex(lineNumber - 1);
        var length = editor.GetLineLength(lineNumber - 1);
        editor.Focus(); editor.Select(start, Math.Max(0, length)); editor.ScrollToLine(lineNumber - 1);
    }

    private void CopyResults_Click(object sender, RoutedEventArgs e)
    {
        var matches = SearchResults.Where(item => !item.IsContext).Select(item => item.Text).ToArray();
        if (matches.Length == 0) return;
        Clipboard.SetText(string.Join(CurrentDocument?.NewLine ?? Environment.NewLine, matches));
        StatusMessage.Text = $"일치한 {matches.Length:N0}개 줄만 클립보드에 복사했습니다.";
    }

    private void ExtractResults_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateResultSource() || CurrentDocument is null || _matchedLineNumbers.Count == 0) return;
        var result = _transformService.ExtractLines(CurrentDocument.Text, _matchedLineNumbers, 0, CurrentDocument.NewLine);
        NewDocument(result.Text);
        StatusMessage.Text = $"{_matchedLineNumbers.Count:N0}개 줄을 새 탭으로 추출했습니다.";
    }

    private void DeleteResults_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateResultSource() || CurrentDocument is null || _matchedLineNumbers.Count == 0) return;
        var result = _transformService.DeleteLines(CurrentDocument.Text, _matchedLineNumbers, CurrentDocument.NewLine);
        ShowTransformPreview(result, "일치 줄 삭제");
    }

    private void TrimOperationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndMarkerPanel is null) return;
        var index = TrimOperationCombo.SelectedIndex;
        var markerOperation = index is >= 0 and <= 3;
        var between = index is 2 or 3;
        MarkerInputsPanel.Visibility = markerOperation ? Visibility.Visible : Visibility.Collapsed;
        EndMarkerPanel.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
        TrimOptionsPanel.Visibility = markerOperation ? Visibility.Visible : Visibility.Collapsed;
        KeepEndCheck.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
        CharacterCountPanel.Visibility = index is 4 or 5 ? Visibility.Visible : Visibility.Collapsed;
        ValueInputPanel.Visibility = index is 6 or 7 or 8 or 9 ? Visibility.Visible : Visibility.Collapsed;
        LineNumberPanel.Visibility = index is 10 or 11 ? Visibility.Visible : Visibility.Collapsed;
        StartNumberPanel.Visibility = index == 10 ? Visibility.Visible : Visibility.Collapsed;
        IncludeBlankLinesCheck.Visibility = index is 6 or 7 or 10 ? Visibility.Visible : Visibility.Collapsed;
        ValueInputLabel.Text = index switch { 6 => "앞에 추가할 텍스트", 7 => "뒤에 추가할 텍스트", 8 => "나눌 구분자", 9 => "줄을 이을 구분자", _ => "텍스트" };
    }

    private void PreviewTrim_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null) return;
        var index = TrimOperationCombo.SelectedIndex;
        TextTransformResult result;
        if (index is >= 0 and <= 3)
        {
            if (string.IsNullOrEmpty(StartMarkerBox.Text)) { ShowInputMessage("시작 기준 텍스트를 입력하세요."); return; }
            if (index is 2 or 3 && string.IsNullOrEmpty(EndMarkerBox.Text)) { ShowInputMessage("종료 기준 텍스트를 입력하세요."); return; }
            var operation = index switch { 1 => TrimOperation.RemoveAfter, 2 => TrimOperation.RemoveBetween, 3 => TrimOperation.KeepBetween, _ => TrimOperation.RemoveBefore };
            var options = new TrimOptions(operation, StartMarkerBox.Text, EndMarkerBox.Text, KeepStartCheck.IsChecked == true, KeepEndCheck.IsChecked == true, TrimMatchCaseCheck.IsChecked == true);
            result = _transformService.Trim(CurrentDocument.Text, options, CurrentDocument.NewLine);
        }
        else if (index is 4 or 5)
        {
            if (!int.TryParse(CharacterCountBox.Text, out var count) || count < 0) { ShowInputMessage("삭제할 글자 수에 0 이상의 정수를 입력하세요."); return; }
            result = _transformService.RemoveCharacters(CurrentDocument.Text, count, index == 4 ? CharacterRemovalSide.Left : CharacterRemovalSide.Right, CurrentDocument.NewLine);
        }
        else if (index is >= 6 and <= 9)
        {
            if (index is 8 or 9 && string.IsNullOrEmpty(ValueInputBox.Text)) { ShowInputMessage("구분자를 입력하세요."); return; }
            result = index switch
            {
                6 => _transformService.AddPrefix(CurrentDocument.Text, ValueInputBox.Text, IncludeBlankLinesCheck.IsChecked == true, CurrentDocument.NewLine),
                7 => _transformService.AddSuffix(CurrentDocument.Text, ValueInputBox.Text, IncludeBlankLinesCheck.IsChecked == true, CurrentDocument.NewLine),
                8 => _transformService.SplitByDelimiter(CurrentDocument.Text, ValueInputBox.Text, CurrentDocument.NewLine),
                _ => _transformService.JoinLines(CurrentDocument.Text, ValueInputBox.Text)
            };
        }
        else
        {
            if (index == 10 && (!int.TryParse(StartNumberBox.Text, out var startNumber) || startNumber < 0)) { ShowInputMessage("시작 번호에 0 이상의 정수를 입력하세요."); return; }
            if (string.IsNullOrEmpty(LineNumberSeparatorBox.Text)) { ShowInputMessage("번호 구분자를 입력하세요."); return; }
            result = index == 10
                ? _transformService.AddLineNumbers(CurrentDocument.Text, int.Parse(StartNumberBox.Text), LineNumberSeparatorBox.Text, IncludeBlankLinesCheck.IsChecked == true, CurrentDocument.NewLine)
                : _transformService.RemoveLineNumbers(CurrentDocument.Text, LineNumberSeparatorBox.Text, CurrentDocument.NewLine);
        }
        ShowTransformPreview(result, $"{(TrimOperationCombo.SelectedItem as ComboBoxItem)?.Content} 미리보기");
    }

    private void ShowInputMessage(string message) => MessageBox.Show(this, message, "유용한 기능", MessageBoxButton.OK, MessageBoxImage.Information);

    private void QuickTransform_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null || sender is not FrameworkElement { Tag: string operation }) return;
        var result = operation switch
        {
            "Duplicate" => _transformService.RemoveDuplicateLines(CurrentDocument.Text, false, CurrentDocument.NewLine),
            "Blank" => _transformService.RemoveBlankLines(CurrentDocument.Text, CurrentDocument.NewLine),
            "Collapse" => _transformService.CollapseBlankLines(CurrentDocument.Text, CurrentDocument.NewLine),
            _ => _transformService.TrimWhitespace(CurrentDocument.Text, CurrentDocument.NewLine)
        };
        ShowTransformPreview(result, ((Button)sender).Content?.ToString() ?? "빠른 정리");
    }

    private void PreviewReplace_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null) return;
        if (string.IsNullOrEmpty(ReplaceFromBox.Text)) { MessageBox.Show(this, "찾을 텍스트를 입력하세요.", "일괄 치환", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        ShowTransformPreview(_transformService.Replace(CurrentDocument.Text, ReplaceFromBox.Text, ReplaceToBox.Text, false, CurrentDocument.NewLine), "치환 미리보기");
    }

    private void ShowTransformPreview(TextTransformResult result, string title)
    {
        _allPreviewRows.Clear();
        PreviewRows.Clear();
        foreach (var item in result.Preview)
            _allPreviewRows.Add(new TransformPreviewRow(item.LineNumber, item.OriginalText, item.ResultText, item.Status switch { TextChangeStatus.Changed => "변경", TextChangeStatus.Skipped => "건너뜀", _ => "유지" }));
        ApplyPreviewFilter();
        _pendingTransformedText = result.Text;
        CaptureResultSource();
        ResultHeader.Text = $"{title}  ·  변경 {result.Summary.ChangedLines:N0} / 건너뜀 {result.Summary.SkippedLines:N0} / 빈 줄 {result.Summary.EmptyResultLines:N0}";
        SearchResultsList.Visibility = Visibility.Collapsed;
        PreviewGrid.Visibility = Visibility.Visible;
        ApplyTransformButton.Visibility = Visibility.Visible;
        ApplyTransformButton.IsEnabled = result.Summary.ChangedLines > 0;
        PreviewFilterCombo.Visibility = Visibility.Visible;
        CopyResultsButton.Visibility = ExtractResultsButton.Visibility = DeleteResultsButton.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultRow.MinHeight = 120;
        ResultSplitterRow.Height = new GridLength(5);
        ResultsMenuItem.IsChecked = true;
        ResultRow.Height = new GridLength(Math.Max(180, _settings.ResultPanelHeight));
        StatusMessage.Text = "변경 전과 변경 후를 확인한 다음 적용하세요.";
    }

    private void ApplyTransform_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateResultSource() || CurrentDocument is null || _pendingTransformedText is null) return;
        var editor = CurrentEditor;
        if (editor is not null)
        {
            editor.BeginChange();
            editor.SelectAll();
            editor.SelectedText = _pendingTransformedText;
            editor.EndChange();
        }
        else
        {
            CurrentDocument.Text = _pendingTransformedText;
        }
        _pendingTransformedText = null;
        ApplyTransformButton.Visibility = Visibility.Collapsed;
        StatusMessage.Text = "텍스트 변경을 적용했습니다. Ctrl+Z로 되돌릴 수 있습니다.";
        UpdateStatus();
    }

    private void ShowSearchResults()
    {
        SearchResultsList.Visibility = Visibility.Visible;
        PreviewGrid.Visibility = Visibility.Collapsed;
        PreviewFilterCombo.Visibility = Visibility.Collapsed;
        ApplyTransformButton.Visibility = Visibility.Collapsed;
        CopyResultsButton.Visibility = ExtractResultsButton.Visibility = DeleteResultsButton.Visibility = Visibility.Visible;
        SetResultActionsEnabled(true);
        ResultPanel.Visibility = Visibility.Visible;
        ResultRow.MinHeight = 120;
        ResultSplitterRow.Height = new GridLength(5);
        ResultsMenuItem.IsChecked = true;
        ResultRow.Height = new GridLength(Math.Max(120, _settings.ResultPanelHeight));
    }

    private void PreviewFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewRows is null) return;
        ApplyPreviewFilter();
    }

    private void ApplyPreviewFilter()
    {
        var filter = (PreviewFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "전체";
        PreviewRows.Clear();
        foreach (var row in _allPreviewRows.Where(row => filter == "전체" || row.Status == filter)) PreviewRows.Add(row);
    }

    private void ResetResults_Click(object sender, RoutedEventArgs e)
    {
        SearchResults.Clear();
        PreviewRows.Clear();
        _allPreviewRows.Clear();
        _matchedLineNumbers = [];
        _pendingTransformedText = null;
        _resultDocument = null;
        _resultSourceText = null;
        _resultInvalidated = false;
        ResultHeader.Text = "검색 결과";
        ResultPanel.Visibility = Visibility.Collapsed;
        ResultRow.MinHeight = 0;
        ResultRow.Height = new GridLength(0);
        ResultSplitterRow.Height = new GridLength(0);
        ResultsMenuItem.IsChecked = false;
        StatusMessage.Text = "결과를 초기화했습니다.";
    }

    private void CaptureResultSource()
    {
        _resultDocument = CurrentDocument;
        _resultSourceText = CurrentDocument?.Text;
        _resultInvalidated = false;
        SetResultActionsEnabled(true);
    }

    private bool TryValidateResultSource()
    {
        var valid = !_resultInvalidated && CurrentDocument is not null && CurrentDocument == _resultDocument && CurrentDocument.Text == _resultSourceText;
        if (valid) return true;
        InvalidateResults("문서가 변경되어 이 결과를 적용할 수 없습니다. 검색 또는 미리보기를 다시 실행하세요.");
        MessageBox.Show(this, "결과를 만든 뒤 문서나 원문이 변경되었습니다.\n현재 문서에서 검색 또는 미리보기를 다시 실행해 주세요.", "결과가 만료됨", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void InvalidateResults(string message)
    {
        if (_resultDocument is null) return;
        _resultInvalidated = true;
        _pendingTransformedText = null;
        SetResultActionsEnabled(false);
        ResultHeader.Text = "결과가 만료되었습니다  ·  다시 실행 필요";
        StatusMessage.Text = message;
    }

    private void SetResultActionsEnabled(bool enabled)
    {
        CopyResultsButton.IsEnabled = enabled;
        ExtractResultsButton.IsEnabled = enabled;
        DeleteResultsButton.IsEnabled = enabled;
        ApplyTransformButton.IsEnabled = enabled;
    }

    private void ToggleTools_Click(object sender, RoutedEventArgs e)
    {
        var show = (sender as MenuItem)?.IsChecked == true;
        ToolColumn.MinWidth = show ? 280 : 0;
        ToolColumn.Width = show ? new GridLength(Math.Max(280, _settings.ToolPanelWidth)) : new GridLength(0);
        ToolSplitterColumn.Width = show ? new GridLength(5) : new GridLength(0);
        ToolPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleResults_Click(object sender, RoutedEventArgs e)
    {
        var show = (sender as MenuItem)?.IsChecked == true;
        ResultRow.MinHeight = show ? 120 : 0;
        ResultRow.Height = show ? new GridLength(Math.Max(120, _settings.ResultPanelHeight)) : new GridLength(0);
        ResultSplitterRow.Height = show ? new GridLength(5) : new GridLength(0);
        ResultPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LightTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("Light");
    private void DarkTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("Dark");
    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme(_settings.Theme == "Dark" ? "Light" : "Dark");

    private void ApplyTheme(string theme)
    {
        _settings.Theme = theme == "Dark" ? "Dark" : "Light";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{_settings.Theme}.xaml", UriKind.Relative) };
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || FontSizeCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var size)) return;
        ApplyEditorFontSize(size, OverflowFontSizeCombo);
    }

    private void OverflowFontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || OverflowFontSizeCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var size)) return;
        ApplyEditorFontSize(size, FontSizeCombo);
    }

    private void ApplyEditorFontSize(double size, ComboBox counterpart)
    {
        _settings.EditorFontSize = size;
        Application.Current.Resources["EditorFontSize"] = size;
        SelectComboItem(counterpart, size.ToString("0"));
    }

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyEditorFont(FontFamilyCombo.SelectedItem?.ToString());
    }

    private void FontFamilyCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyEditorFont(FontFamilyCombo.Text);

    private void OverflowFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyEditorFont(OverflowFontFamilyCombo.SelectedItem?.ToString());
    }

    private void OverflowFontFamilyCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyEditorFont(OverflowFontFamilyCombo.Text);

    private void ApplyEditorFont(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) return;
        var installed = FontFamilyCombo.Items.Cast<string>().FirstOrDefault(item => string.Equals(item, familyName.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (installed is null) return;
        _settings.EditorFontFamily = installed;
        FontFamilyCombo.Text = installed;
        OverflowFontFamilyCombo.Text = installed;
        Application.Current.Resources["EditorFontFamily"] = new FontFamily(installed);
    }

    private void LoadInstalledFonts()
    {
        string[][] preferredAliases =
        {
            ["맑은 고딕", "Malgun Gothic"], ["D2Coding"], ["나눔고딕", "NanumGothic"],
            ["Noto Sans KR"], ["Pretendard"], ["Cascadia Mono"], ["Consolas"], ["JetBrains Mono"],
            ["굴림", "Gulim"], ["돋움", "Dotum"], ["바탕", "Batang"], ["궁서", "Gungsuh"], ["Segoe UI"]
        };
        var installed = Fonts.SystemFontFamilies.Select(family => family.Source).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var preferred = preferredAliases
            .Select(aliases => aliases.FirstOrDefault(alias => installed.Contains(alias, StringComparer.CurrentCultureIgnoreCase)))
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        var orderedFonts = preferred
            .Concat(installed.Where(name => !preferred.Contains(name, StringComparer.CurrentCultureIgnoreCase)).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            .ToArray();
        FontFamilyCombo.ItemsSource = orderedFonts;
        OverflowFontFamilyCombo.ItemsSource = orderedFonts;
        var selected = orderedFonts.FirstOrDefault(name => string.Equals(name, _settings.EditorFontFamily, StringComparison.CurrentCultureIgnoreCase))
            ?? preferredAliases[0].Select(alias => orderedFonts.FirstOrDefault(name => string.Equals(name, alias, StringComparison.CurrentCultureIgnoreCase))).FirstOrDefault(name => name is not null)
            ?? orderedFonts.FirstOrDefault(name => string.Equals(name, "Consolas", StringComparison.CurrentCultureIgnoreCase))
            ?? orderedFonts.FirstOrDefault()
            ?? "Consolas";
        _settings.EditorFontFamily = selected;
        Application.Current.Resources["EditorFontFamily"] = new FontFamily(selected);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var useOverflow = e.NewSize.Width < 1180;
        ToolbarFontPanel.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
        ToolbarOverflowPanel.Visibility = useOverflow ? Visibility.Visible : Visibility.Collapsed;
        if (!useOverflow) ToolbarOverflowPopup.IsOpen = false;
    }

    private void ToolbarOverflowButton_Click(object sender, RoutedEventArgs e) => ToolbarOverflowPopup.IsOpen = !ToolbarOverflowPopup.IsOpen;

    private void AddRecentFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        _settings.RecentFiles.RemoveAll(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFiles.Insert(0, fullPath);
        if (_settings.RecentFiles.Count > 10) _settings.RecentFiles.RemoveRange(10, _settings.RecentFiles.Count - 10);
        BuildRecentFilesMenu();
    }

    private void BuildRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();
        foreach (var path in _settings.RecentFiles.Where(File.Exists))
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, Tag = path };
            item.Click += async (_, _) => await OpenFileAsync(path);
            RecentFilesMenu.Items.Add(item);
        }
        if (RecentFilesMenu.Items.Count == 0) RecentFilesMenu.Items.Add(new MenuItem { Header = "최근 파일 없음", IsEnabled = false });
    }

    private void SaveSettings()
    {
        _settings.WindowWidth = RestoreBounds.Width;
        _settings.WindowHeight = RestoreBounds.Height;
        _settings.WindowLeft = RestoreBounds.Left;
        _settings.WindowTop = RestoreBounds.Top;
        _settings.WindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        _settings.ToolPanelVisible = ToolPanel.Visibility == Visibility.Visible;
        _settings.ResultPanelVisible = ResultPanel.Visibility == Visibility.Visible;
        if (ToolColumn.Width.Value > 0) _settings.ToolPanelWidth = ToolColumn.ActualWidth;
        if (ResultRow.Height.Value > 0) _settings.ResultPanelHeight = ResultRow.ActualHeight;
        if (JsonSerializer.Serialize(_settings) == _settingsSnapshot) return;
        try { SettingsService.Save(_settings); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RestoreWindowPlacement()
    {
        var workArea = SystemParameters.WorkArea;
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }
        if (_settings.WindowState == "Maximized") WindowState = WindowState.Maximized;
    }

    private void RestorePanelVisibility()
    {
        ToolsMenuItem.IsChecked = _settings.ToolPanelVisible;
        ToolColumn.MinWidth = _settings.ToolPanelVisible ? 280 : 0;
        ToolColumn.Width = _settings.ToolPanelVisible ? new GridLength(Math.Max(280, _settings.ToolPanelWidth)) : new GridLength(0);
        ToolSplitterColumn.Width = _settings.ToolPanelVisible ? new GridLength(5) : new GridLength(0);
        ToolPanel.Visibility = _settings.ToolPanelVisible ? Visibility.Visible : Visibility.Collapsed;

        ResultsMenuItem.IsChecked = _settings.ResultPanelVisible;
        ResultRow.MinHeight = _settings.ResultPanelVisible ? 120 : 0;
        ResultRow.Height = _settings.ResultPanelVisible ? new GridLength(Math.Max(120, _settings.ResultPanelHeight)) : new GridLength(0);
        ResultSplitterRow.Height = _settings.ResultPanelVisible ? new GridLength(5) : new GridLength(0);
        ResultPanel.Visibility = _settings.ResultPanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "조건 검색\n  조건과 그룹을 추가해 AND/OR/제외 검색을 구성하세요.\n\n텍스트 정리\n  자르기 또는 빠른 정리 후 미리보기를 확인하고 적용하세요.\n\n단축키\n  Ctrl+N 새 문서 · Ctrl+O 열기 · Ctrl+S 저장 · Ctrl+F 조건 검색",
        "나만의 텍스트 편집기", MessageBoxButton.OK, MessageBoxImage.Information);

    private static int SelectedNumber(ComboBox comboBox) => int.TryParse((comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var result) ? result : 0;

    private static int CountLines(string text)
    {
        var count = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                count++;
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n')
            {
                count++;
            }
        }
        return count;
    }

    private static void SelectComboItem(ComboBox comboBox, string value)
    {
        foreach (var candidate in comboBox.Items)
        {
            var display = candidate is ComboBoxItem item ? item.Content?.ToString() : candidate?.ToString();
            if (string.Equals(display, value, StringComparison.OrdinalIgnoreCase)) { comboBox.SelectedItem = candidate; return; }
        }
    }

}
