using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyTextEditor.Core.Macros;
using Panel = System.Windows.Controls.Panel;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace MyTextEditor.Macros;

public sealed class MacroWindow : Window
{
    private readonly MacroWindowCallbacks _callbacks;
    private readonly MacroStore _store;
    private List<MacroDefinition> _saved = [];
    private MacroDefinition _draft = new() { Name = "새 매크로" };
    private string _baseline = "";
    private readonly ListBox _library = new(), _steps = new(), _statistics = new();
    private readonly TextBox _name = new();
    private readonly StackPanel _fields = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _before = new() { IsReadOnly = true, AcceptsReturn = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _after = new() { IsReadOnly = true, AcceptsReturn = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _apply = new() { Content = "최종 결과 적용", IsEnabled = false };
    private readonly Button _preview = new() { Content = "미리보기" }, _run = new() { Content = "바로 실행" }, _cancel = new() { Content = "실행 취소", IsEnabled = false };
    private CancellationTokenSource? _execution, _detailCancellation;
    private MacroDocumentSnapshot? _snapshot;
    private MacroDefinition? _executed;
    private string? _result;
    private bool _binding, _prepared, _closed, _loadFailed;
    private int _detailGeneration;
    public bool HasUnsavedChanges => MacroJsonCodec.Serialize(_draft) != _baseline;

    private static readonly Dictionary<MacroOperation, string> Names = new()
    {
        [MacroOperation.Search]="조건 검색", [MacroOperation.DeleteTargetLines]="대상 줄 삭제", [MacroOperation.KeepTargetLines]="대상 줄만 남기기",
        [MacroOperation.RemoveBefore]="기준 앞쪽 제거", [MacroOperation.RemoveAfter]="기준 뒤쪽 제거", [MacroOperation.RemoveBetween]="두 기준 사이 삭제", [MacroOperation.KeepBetween]="두 기준 사이만 남기기",
        [MacroOperation.RemoveCharactersLeft]="왼쪽 N글자 제거", [MacroOperation.RemoveCharactersRight]="오른쪽 N글자 제거", [MacroOperation.AddPrefix]="접두사 추가", [MacroOperation.AddSuffix]="접미사 추가",
        [MacroOperation.SplitByDelimiter]="구분자로 나누기", [MacroOperation.JoinLines]="구분자로 합치기", [MacroOperation.AddLineNumbers]="줄 번호 붙이기", [MacroOperation.RemoveLineNumbers]="줄 번호 제거",
        [MacroOperation.RemoveLinesContaining]="특정 문장 포함 줄 삭제", [MacroOperation.Replace]="일괄 치환", [MacroOperation.TrimWhitespace]="앞뒤 공백 제거", [MacroOperation.RemoveDuplicateLines]="중복 줄 제거",
        [MacroOperation.RemoveBlankLines]="빈 줄 제거", [MacroOperation.CollapseBlankLines]="연속 빈 줄 합치기", [MacroOperation.CleanupLog]="로그 정리"
    };

    public MacroWindow(MacroWindowCallbacks callbacks, MacroStore? store = null)
    {
        _callbacks = callbacks; _store = store ?? new MacroStore();
        Title = "작업 매크로 — MyTextEditor"; Width = 1180; Height = 800; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "작업 매크로", FontSize = 22, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "동작을 순서대로 조립하세요. 검색한 줄은 다음 검색 전까지 추적하며, 실행 전체를 Ctrl+Z 한 번으로 되돌릴 수 있습니다.", Margin = new Thickness(0, 6, 0, 10), TextWrapping = TextWrapping.Wrap });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var commands = Row(); footer.Children.Add(commands);
        foreach (var button in new[] { _preview, _run, _apply, _cancel }) { button.Margin = new Thickness(0, 8, 8, 8); commands.Children.Add(button); }
        footer.Children.Add(_status);
        _preview.Click += async (_, _) => await ExecuteAsync(false); _run.Click += async (_, _) => await ExecuteAsync(true);
        _apply.Click += (_, _) => Apply(); _cancel.Click += (_, _) => _execution?.Cancel();
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) }); layout.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(layout);
        var libraryPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) }; layout.Children.Add(libraryPanel);
        var libraryButtons = new WrapPanel(); DockPanel.SetDock(libraryButtons, Dock.Bottom); libraryPanel.Children.Add(libraryButtons);
        AddButton(libraryButtons, "새로", New); AddButton(libraryButtons, "복제", Duplicate); AddButton(libraryButtons, "삭제", Delete);
        AddButton(libraryButtons, "가져오기", Import); AddButton(libraryButtons, "내보내기", Export);
        libraryPanel.Children.Add(_library); _library.DisplayMemberPath = "Name";
        _library.SelectionChanged += (_, _) => { if (!_binding && _library.SelectedItem is MacroDefinition selected) SwitchTo(selected); };
        var work = new Grid(); Grid.SetColumn(work, 1); layout.Children.Add(work);
        work.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); work.RowDefinitions.Add(new RowDefinition()); work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(220) });
        var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; work.Children.Add(nameRow);
        var save = new Button { Content = "매크로 저장", Margin = new Thickness(8,0,0,0) }; DockPanel.SetDock(save, Dock.Right); nameRow.Children.Add(save); save.Click += (_, _) => Save(); nameRow.Children.Add(_name);
        _name.TextChanged += (_, _) => { if (!_binding) _draft.Name = _name.Text; };
        var editor = new Grid(); Grid.SetRow(editor, 1); work.Children.Add(editor); editor.ColumnDefinitions.Add(new ColumnDefinition()); editor.ColumnDefinitions.Add(new ColumnDefinition());
        var stepPanel = new DockPanel { Margin = new Thickness(0, 0, 10, 0) }; editor.Children.Add(stepPanel);
        var stepCommands = new WrapPanel(); DockPanel.SetDock(stepCommands, Dock.Bottom); stepPanel.Children.Add(stepCommands);
        AddButton(stepCommands, "+ 단계", () => { _draft.Steps.Add(new MacroStep()); RefreshSteps(_draft.Steps.Count - 1); });
        AddButton(stepCommands, "복제", () => { int i = _steps.SelectedIndex; if (i < 0) return; var wrapper = new MacroDefinition { Name = "복제", Steps = [_draft.Steps[i]] }; _draft.Steps.Insert(i + 1, Clone(wrapper).Steps[0]); RefreshSteps(i + 1); });
        AddButton(stepCommands, "삭제", () => { int i = _steps.SelectedIndex; if (i < 0) return; _draft.Steps.RemoveAt(i); RefreshSteps(Math.Min(i, _draft.Steps.Count - 1)); });
        AddButton(stepCommands, "↑", () => Move(-1)); AddButton(stepCommands, "↓", () => Move(1));
        stepPanel.Children.Add(_steps); _steps.SelectionChanged += (_, _) => ShowFields();
        var scroll = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); editor.Children.Add(scroll);
        var resultPanel = new Grid { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(resultPanel, 2); work.Children.Add(resultPanel);
        resultPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) }); resultPanel.ColumnDefinitions.Add(new ColumnDefinition()); resultPanel.ColumnDefinitions.Add(new ColumnDefinition());
        resultPanel.Children.Add(_statistics);
        var beforePanel = new DockPanel(); var afterPanel = new DockPanel();
        var beforeLabel = new TextBlock { Text = "변경 전 · 처음 20,000자", Margin = new Thickness(4) };
        var afterLabel = new TextBlock { Text = "변경 후 · 처음 20,000자", Margin = new Thickness(4) };
        DockPanel.SetDock(beforeLabel, Dock.Top); DockPanel.SetDock(afterLabel, Dock.Top);
        beforePanel.Children.Add(beforeLabel); beforePanel.Children.Add(_before); afterPanel.Children.Add(afterLabel); afterPanel.Children.Add(_after);
        Grid.SetColumn(beforePanel, 1); Grid.SetColumn(afterPanel, 2); resultPanel.Children.Add(beforePanel); resultPanel.Children.Add(afterPanel);
        _before.ToolTip = "선택 단계 변경 전 (처음 20,000자)"; _after.ToolTip = "선택 단계 변경 후 / 최종 결과 (처음 20,000자)";
        _statistics.SelectionChanged += async (_, _) => await ShowDetailAsync();
        try { _saved = _store.Load(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { _loadFailed = true; SetStatus("매크로 파일을 읽지 못했습니다. 기존 파일 보호를 위해 저장을 중단합니다: " + ex.Message); }
        LoadDraft(_saved.FirstOrDefault() ?? _draft); RefreshLibrary();
        Closing += OnClosing; Closed += (_, _) => { _closed = true; _execution?.Cancel(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) { _callbacks.ShowHelp?.Invoke(); e.Handled = true; } };
    }

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal };
    private static void AddButton(Panel panel, string title, Action action) { var b = new Button { Content = title, Margin = new Thickness(0, 3, 5, 3) }; b.Click += (_, _) => action(); panel.Children.Add(b); }
    private static MacroDefinition Clone(MacroDefinition macro) => System.Text.Json.JsonSerializer.Deserialize<MacroDefinition>(System.Text.Json.JsonSerializer.Serialize(macro))!;
    private void SetStatus(string value) { _status.Text = value; _callbacks.ReportStatus?.Invoke(value); }
    private void RefreshLibrary() { _binding = true; _library.ItemsSource = null; _library.ItemsSource = _saved; _library.SelectedItem = _saved.FirstOrDefault(m => m.Id == _draft.Id); _binding = false; }
    private void LoadDraft(MacroDefinition macro) { _execution?.Cancel(); _apply.IsEnabled = false; _result = null; _statistics.ItemsSource = null; _before.Clear(); _after.Clear(); ++_detailGeneration; _detailCancellation?.Cancel(); _draft = Clone(macro); _baseline = MacroJsonCodec.Serialize(_draft); _binding = true; _name.Text = _draft.Name; _binding = false; RefreshSteps(_draft.Steps.Count > 0 ? 0 : -1); }
    private static string StepTitle(MacroStep s, int index)
    {
        string value = s.Operation == MacroOperation.Search ? string.Join(" / ", s.AllTerms.Concat(s.AnyTerms).Concat(s.ExcludeTerms)) : s.Value;
        return $"{index + 1}. {(s.Enabled ? "" : "[꺼짐] ")}{(s.Target == MacroTarget.WholeDocument ? "전체" : "찾은 줄")} · {Names[s.Operation]} {value}";
    }
    private void UpdateLabels()
    {
        for (int i = 0; i < _draft.Steps.Count && i < _steps.Items.Count; i++)
            if (_steps.Items[i] is TextBlock label) label.Text = StepTitle(_draft.Steps[i], i);
    }
    private void RefreshSteps(int index)
    {
        _steps.ItemsSource = _draft.Steps.Select((s,i) => new TextBlock { Text = StepTitle(s,i), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2,4,2,4) }).ToList();
        _steps.SelectedIndex = index; ShowFields();
    }
    private void Move(int delta) { int i = _steps.SelectedIndex, j = i + delta; if (i < 0 || j < 0 || j >= _draft.Steps.Count) return; (_draft.Steps[i], _draft.Steps[j]) = (_draft.Steps[j], _draft.Steps[i]); RefreshSteps(j); }
    private void SwitchTo(MacroDefinition macro) { if (!ConfirmDraft()) { RefreshLibrary(); return; } LoadDraft(macro); RefreshLibrary(); }
    private void New() { if (!ConfirmDraft()) return; LoadDraft(new MacroDefinition { Name = "새 매크로" }); RefreshLibrary(); }
    private void Duplicate() { if (!ConfirmDraft()) return; var copy = Clone(_draft); copy.Id = Guid.NewGuid(); copy.Name += " 복사"; LoadDraft(copy); _baseline = ""; RefreshLibrary(); }
    private bool Save()
    {
        if (_loadFailed) { SetStatus("기존 매크로 파일의 읽기 오류를 해결한 뒤 다시 여세요. 현재 매크로는 내보내기로 보관할 수 있습니다."); return false; }
        if (string.IsNullOrWhiteSpace(_draft.Name)) { SetStatus("매크로 이름을 입력하세요."); _name.Focus(); return false; }
        var errors = new TextMacroRunner().Validate(_draft); if (errors.Count > 0) { SetStatus(string.Join("\n", errors)); return false; }
        try { var list = _saved.Where(m => m.Id != _draft.Id).ToList(); list.Add(Clone(_draft)); _store.Save(list); _saved = list; _baseline = MacroJsonCodec.Serialize(_draft); RefreshLibrary(); SetStatus("매크로를 저장했습니다."); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { SetStatus("저장 실패: " + ex.Message); return false; }
    }
    private bool ConfirmDraft() { if (!HasUnsavedChanges) return true; var answer = MessageBox.Show(this, "매크로 편집 내용을 저장할까요?\n예: 저장 / 아니요: 버리기 / 취소: 계속 편집", "매크로 저장", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && Save(); }
    private void Delete() { if (_loadFailed) return; if (MessageBox.Show(this, "이 매크로를 삭제할까요?", "매크로 삭제", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; try { var list = _saved.Where(m => m.Id != _draft.Id).ToList(); _store.Save(list); _saved = list; LoadDraft(_saved.FirstOrDefault() ?? new MacroDefinition { Name = "새 매크로" }); RefreshLibrary(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("삭제 실패: " + ex.Message); } }
    private void Import() { if (!ConfirmDraft()) return; var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "매크로 JSON|*.json" }; if (dialog.ShowDialog(this) != true) return; try { var macro = MacroJsonCodec.Deserialize(File.ReadAllText(dialog.FileName)); macro.Id = Guid.NewGuid(); LoadDraft(macro); _baseline = ""; Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { SetStatus("가져오기 실패: " + ex.Message); } }
    private void Export() { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "매크로 JSON|*.json", FileName = "macro.json" }; if (dialog.ShowDialog(this) != true) return; try { File.WriteAllText(dialog.FileName, MacroJsonCodec.Serialize(_draft)); SetStatus("매크로를 내보냈습니다."); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { SetStatus("내보내기 실패: " + ex.Message); } }
    private void ShowFields()
    {
        _fields.Children.Clear(); int index = _steps.SelectedIndex; if (index < 0 || index >= _draft.Steps.Count) return;
        var step = _draft.Steps[index];
        void Update() => UpdateLabels();
        void Toggle(string label, bool value, Action<bool> setter) { var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0,5,0,5) }; _fields.Children.Add(c); c.Click += (_,_) => setter(c.IsChecked == true); }
        void Input(string label, string value, Action<string> setter, bool multiline = false) { _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,8,0,3) }); var t = new TextBox { Text = value, AcceptsReturn = multiline, MinHeight = multiline ? 58 : 28, ToolTip = label }; _fields.Children.Add(t); t.TextChanged += (_,_) => { setter(t.Text); UpdateLabels(); }; }
        Toggle("이 단계 사용", step.Enabled, v => { step.Enabled = v; Update(); });
        var operation = new ComboBox { ItemsSource = Names, DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = step.Operation, Margin = new Thickness(0,5,0,5) }; _fields.Children.Add(operation);
        operation.SelectionChanged += (_,_) => { if (operation.SelectedValue is MacroOperation o) { step.Operation = o; RefreshSteps(index); } };
        var targets = new[] { "전체 문서", "최근 검색에서 찾은 줄" }; var target = new ComboBox { ItemsSource = targets, SelectedIndex = step.Target == MacroTarget.WholeDocument ? 0 : 1, Margin = new Thickness(0,5,0,5) }; _fields.Children.Add(target);
        target.SelectionChanged += (_,_) => { step.Target = target.SelectedIndex == 0 ? MacroTarget.WholeDocument : MacroTarget.MatchedLines; Update(); };
        var op = step.Operation;
        if (op == MacroOperation.Search)
        {
            Input("모두 포함 (한 줄에 한 검색어)", string.Join("\n",step.AllTerms), s => step.AllTerms = Terms(s), true);
            Input("하나라도 포함 (한 줄에 한 검색어)", string.Join("\n",step.AnyTerms), s => step.AnyTerms = Terms(s), true);
            Input("제외 (한 줄에 한 검색어)", string.Join("\n",step.ExcludeTerms), s => step.ExcludeTerms = Terms(s), true);
            Toggle("완전한 단어", step.WholeWord, v => step.WholeWord = v);
        }
        if (op is MacroOperation.RemoveBefore or MacroOperation.RemoveAfter or MacroOperation.RemoveBetween or MacroOperation.KeepBetween)
        {
            Input("시작 기준 (예: [)", step.Value, s => step.Value = s);
            if (op is MacroOperation.RemoveBetween or MacroOperation.KeepBetween) Input("종료 기준 (예: ])", step.EndMarker, s => step.EndMarker = s);
            Toggle("시작 기준 유지", step.KeepStart, v => step.KeepStart = v);
            if (op is MacroOperation.RemoveBetween or MacroOperation.KeepBetween) Toggle("종료 기준 유지", step.KeepEnd, v => step.KeepEnd = v);
        }
        if (op is MacroOperation.AddPrefix or MacroOperation.AddSuffix) { Input("붙일 문자열",step.Value,s=>step.Value=s); Toggle("빈 줄 포함",step.IncludeBlankLines,v=>step.IncludeBlankLines=v); }
        if (op is MacroOperation.SplitByDelimiter or MacroOperation.JoinLines or MacroOperation.AddLineNumbers or MacroOperation.RemoveLineNumbers) Input("구분자 (공백도 그대로 사용)",step.Value,s=>step.Value=s);
        if (op is MacroOperation.Replace or MacroOperation.RemoveLinesContaining) Input("찾을 문장",step.Value,s=>step.Value=s);
        if (op == MacroOperation.Replace) Input("바꿀 내용 (비워 두면 제거)",step.Replacement,s=>step.Replacement=s);
        if (op is MacroOperation.RemoveCharactersLeft or MacroOperation.RemoveCharactersRight) Input("제거할 문자 수",step.Count.ToString(),s=>step.Count=int.TryParse(s,out int n)?n:-1);
        if (op == MacroOperation.AddLineNumbers) Toggle("빈 줄 포함",step.IncludeBlankLines,v=>step.IncludeBlankLines=v);
        if (op == MacroOperation.AddLineNumbers) Input("시작 번호",step.StartNumber.ToString(),s=>step.StartNumber=int.TryParse(s,out int n)?n:-1);
        if (op is MacroOperation.Search or MacroOperation.Replace or MacroOperation.RemoveLinesContaining or MacroOperation.RemoveBefore or MacroOperation.RemoveAfter or MacroOperation.RemoveBetween or MacroOperation.KeepBetween or MacroOperation.RemoveDuplicateLines) Toggle("대소문자 구분",step.MatchCase,v=>step.MatchCase=v);
        _fields.Children.Add(new TextBlock { Text = "찾은 줄 대상은 앞에서 실행한 검색이 필요합니다. 검색 결과가 없으면 이 단계는 건너뜁니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,14,0,0) });
    }
    private static List<string> Terms(string text) => text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Where(s=>!string.IsNullOrWhiteSpace(s)).ToList();

    private async Task ExecuteAsync(bool direct)
    {
        if (_execution != null) return;
        MacroDefinition macro;
        try { macro = Clone(_draft); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { SetStatus(ex.Message); return; }
        var errors = new TextMacroRunner().Validate(macro); if (errors.Count > 0) { SetStatus(string.Join("\n",errors)); return; }
        var snapshot = _callbacks.GetCurrentDocument(); if (snapshot == null) { SetStatus("실행할 문서를 먼저 여세요."); return; }
        _execution = new CancellationTokenSource(); var cancellation = _execution; _apply.IsEnabled = false; _preview.IsEnabled = _run.IsEnabled = false; _cancel.IsEnabled = true; _result = null; _statistics.ItemsSource = null; _before.Clear(); _after.Clear(); ++_detailGeneration; _detailCancellation?.Cancel();
        try
        {
            var progress = new Progress<MacroStepResult>(s => { if (!_closed) SetStatus($"{snapshot.DisplayName} · {s.StepIndex + 1}/{macro.Steps.Count} 단계 완료"); });
            var result = await Task.Run(() => new TextMacroRunner().Run(snapshot.Text,snapshot.NewLine,macro,cancellation.Token,progress),cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested) return;
            _snapshot = snapshot; _executed = macro; _result = result.Text;
            _statistics.ItemsSource = result.Steps.Select(s=>new StageItem(s.StepIndex,$"{s.StepIndex+1}. {Names[s.Operation]}\n처리 {s.ProcessedLines} · 변경 {s.ChangedLines}\n일치 {s.MatchedLines} · 건너뜀 {s.SkippedLines}")).ToList(); _statistics.DisplayMemberPath = "Label";
            _before.Text = PreviewText(snapshot.Text); _after.Text = PreviewText(result.Text);
            _apply.IsEnabled = result.Text != snapshot.Text;
            SetStatus(_apply.IsEnabled ? $"매크로 [{macro.Name}] 실행 완료. 최종 결과를 적용하거나 단계를 선택해 전후를 확인하세요. 표시 내용은 처음 20,000자입니다." : "변경 사항이 없습니다.");
            if (direct && _apply.IsEnabled) Apply();
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus("실행을 취소했습니다. 원문은 그대로 유지됩니다."); }
        catch (Exception ex) { if (!_closed) SetStatus("매크로 실행 실패: " + ex.Message); }
        finally { cancellation.Dispose(); if (ReferenceEquals(_execution,cancellation)) _execution = null; if (!_closed) { _preview.IsEnabled = _run.IsEnabled = true; _cancel.IsEnabled = false; } }
    }
    private void Apply()
    {
        if (_snapshot == null || _result == null || !_apply.IsEnabled) return;
        bool success = _callbacks.ApplyResult(_snapshot.DocumentId,_snapshot.Revision,_result); _apply.IsEnabled = false;
        SetStatus(success ? "매크로를 적용했습니다. Ctrl+Z 한 번으로 복원할 수 있습니다." : "원본이 수정되었거나 닫혀 적용할 수 없습니다. 다시 실행하세요.");
    }
    private async Task ShowDetailAsync()
    {
        if (_statistics.SelectedItem is not StageItem stage || _snapshot == null || _executed == null || _execution != null) return;
        int generation = ++_detailGeneration; _detailCancellation?.Cancel(); var detailCancellation = new CancellationTokenSource(); _detailCancellation = detailCancellation; var snapshot = _snapshot; var macro = _executed;
        try
        {
            var detail = await Task.Run(() => {
                var runner = new TextMacroRunner();
                string before = stage.Index == 0 ? snapshot.Text : runner.Run(snapshot.Text,snapshot.NewLine,macro,cancellationToken:detailCancellation.Token,stopAfterStep:stage.Index-1).Text;
                string after = runner.Run(snapshot.Text,snapshot.NewLine,macro,cancellationToken:detailCancellation.Token,stopAfterStep:stage.Index).Text;
                return (Before:PreviewText(before),After:PreviewText(after));
            });
            if (!_closed && generation == _detailGeneration) { _before.Text = detail.Before; _after.Text = detail.After; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && generation == _detailGeneration) SetStatus("단계 미리보기 실패: " + ex.Message); }
        finally { if (ReferenceEquals(_detailCancellation,detailCancellation)) _detailCancellation = null; detailCancellation.Dispose(); }
    }
    private static string PreviewText(string value) => value.Length <= 20000 ? value : value[..20000] + "\n… (표시 생략, 실제 결과에는 전체 내용이 포함됩니다)";
    private sealed record StageItem(int Index,string Label);
    public bool PrepareClose() { if (_prepared) return true; if (!ConfirmDraft()) return false; _prepared = true; _execution?.Cancel(); ++_detailGeneration; _detailCancellation?.Cancel(); return true; }
    public void CancelPreparedClose() => _prepared = false;
    private void OnClosing(object? sender, CancelEventArgs e) { if (!PrepareClose()) e.Cancel = true; }
}







