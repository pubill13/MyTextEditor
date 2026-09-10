using System.Windows;
using System.Windows.Input;

namespace MyTextEditor.Help;

public partial class HelpWindow : Window
{
    private readonly IReadOnlyList<HelpTopic> _topics = HelpCatalog.Topics;
    private readonly HelpWindowPlacement? _initialPlacement;
    private bool _loaded;

    public HelpWindow(HelpWindowPlacement? placement = null)
    {
        _initialPlacement = placement;
        InitializeComponent();
        ShowTopics(_topics);
    }

    public event Action<HelpWindowPlacement>? PlacementChangedByUser;

    public HelpWindowPlacement CurrentPlacement => new(ActualWidth, ActualHeight, Left, Top);

    public void FocusSearch()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyInitialPlacement();
        _loaded = true;
        if (TopicList.SelectedItem is null && TopicList.Items.Count > 0)
            TopicList.SelectedIndex = 0;
    }

    private void ApplyInitialPlacement()
    {
        if (_initialPlacement is null)
            return;

        if (double.IsFinite(_initialPlacement.Width))
            Width = Math.Max(MinWidth, _initialPlacement.Width);
        if (double.IsFinite(_initialPlacement.Height))
            Height = Math.Max(MinHeight, _initialPlacement.Height);

        if (_initialPlacement.Left is not double left || _initialPlacement.Top is not double top ||
            !double.IsFinite(left) || !double.IsFinite(top))
            return;

        var visibleLeft = Math.Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120);
        var visibleTop = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = visibleLeft;
        Top = visibleTop;
    }

    private void Window_PlacementChanged(object? sender, EventArgs e)
    {
        if (_loaded && WindowState == WindowState.Normal)
            PlacementChangedByUser?.Invoke(CurrentPlacement);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var filtered = HelpCatalog.Search(_topics, SearchBox.Text);
        ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTopics(filtered);
    }

    private void ShowTopics(IReadOnlyList<HelpTopic> topics)
    {
        var previous = TopicList.SelectedItem as HelpTopic;
        TopicList.ItemsSource = topics;
        NoResultsPanel.Visibility = topics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TopicScroll.Visibility = topics.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (topics.Count == 0)
        {
            ShowTopic(null);
            return;
        }

        TopicList.SelectedItem = previous is not null && topics.Contains(previous) ? previous : topics[0];
    }

    private void TopicList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ShowTopic(TopicList.SelectedItem as HelpTopic);

    private void ShowTopic(HelpTopic? topic)
    {
        TopicTitle.Text = topic?.Title ?? string.Empty;
        TopicSummary.Text = topic?.Summary ?? string.Empty;
        SectionList.ItemsSource = topic?.Sections;
        TopicScroll.ScrollToTop();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    internal static IReadOnlyList<HelpTopic> CreateTopics() =>
    [
        Topic("시작하기와 문서 탭", "새 문서를 만들고 파일을 열어 여러 문서를 탭으로 편집합니다.", ["파일", "저장", "탭", "드롭"],
            Section("문서 열기", "파일 메뉴나 상단 열기 버튼을 사용합니다. 탐색기에서 파일 하나 또는 여러 개를 일반 편집기 창으로 끌어 놓아도 각 파일이 별도 문서 탭으로 열립니다.", "Ctrl+N  새 문서\nCtrl+O  파일 열기\nCtrl+S  저장\nCtrl+Shift+S  다른 이름으로 저장"),
            Section("탭 관리", "탭의 점 표시는 저장하지 않은 변경이 있다는 뜻입니다. 수정한 탭을 닫을 때 저장 여부를 선택할 수 있으며 취소하면 내용이 그대로 유지됩니다.", "Ctrl+W / Ctrl+F4  현재 탭 닫기\nCtrl+Shift+W  모든 문서 닫기\nCtrl+Tab  다음 탭\nCtrl+Shift+Tab  이전 탭")),

        Topic("대용량 로그와 인코딩", "큰 로그도 빠르게 열고 원래 인코딩과 줄바꿈을 보존합니다.", ["대용량", "UTF-8", "UTF-16", "CP949", "CRLF", "LF"],
            Section("파일 형식 보존", "UTF-8, UTF-16 BOM과 한국어 Windows 인코딩을 감지합니다. 저장할 때 기존 인코딩과 CRLF·LF·CR 줄바꿈을 유지하며 표현할 수 없는 문자가 있으면 UTF-8 저장을 안내합니다."),
            Section("큰 파일 사용", "파일을 여는 동안 상태바에서 진행 상황과 실제 로딩 시간을 확인할 수 있습니다. 검색 결과는 일치한 줄과 필요한 문맥만 보관하므로 원문 전체를 결과마다 복제하지 않습니다.")),

        Topic("검색 조건과 최근 검색", "모두 포함·하나라도 포함·제외 조건을 조합해 필요한 줄을 찾습니다.", ["검색", "AND", "OR", "제외", "최근 검색"],
            Section("조건 입력", "쉼표로 단어를 나눕니다. ‘모두 포함’의 단어는 전부 있어야 하고, ‘하나라도 포함’은 그중 하나가 있으면 됩니다. ‘제외’ 단어가 있는 줄은 결과에서 빠집니다.", "모두 포함: AAA, BBB\n제외: CC\n→ AAA와 BBB가 모두 있고 CC는 없는 줄"),
            Section("검색 옵션", "대소문자 구분, 완전한 단어, 앞뒤 문맥 줄 수를 필요에 맞게 설정합니다. Enter로 검색할 수 있고 최근 검색 메뉴에서 조건 전체와 옵션을 다시 불러올 수 있습니다.", "Ctrl+F  검색 조건으로 이동\nF3 / Shift+F3  다음 / 이전 검색 결과")),

        Topic("검색 결과 탭", "검색할 때마다 독립된 결과 탭이 생기며 이전 검색 결과도 계속 사용할 수 있습니다.", ["결과", "복사", "추출", "삭제", "스냅샷"],
            Section("결과 탐색과 복사", "행을 더블클릭하거나 Enter를 누르면 원문의 해당 줄로 이동합니다. 여러 행을 선택해 복사할 수 있으며 줄 번호 포함 여부도 선택할 수 있습니다. 전체 복사는 일치 행만 포함합니다.", "Ctrl+C  선택 결과 복사\n표시 형식: 123: 원문"),
            Section("추출과 삭제", "선택 또는 전체 일치 행을 새 문서로 추출하거나 삭제 미리보기를 만들 수 있습니다. 원문이 수정되거나 닫힌 오래된 결과는 스냅샷 복사는 가능하지만 원문 이동과 삭제는 차단됩니다.")),

        Topic("자르기와 사이 작업", "각 줄에서 기준 문자열의 앞·뒤 또는 두 기준 사이를 처리합니다.", ["머리", "꼬리", "사이", "기준"],
            Section("기준 앞뒤 제거", "‘기준 앞쪽 제거’는 첫 기준 앞을, ‘기준 뒤쪽 제거’는 첫 기준 뒤를 지웁니다. 기준 문자열을 결과에 남길지도 선택할 수 있습니다.", "원문: INFO | 실제 내용\n‘|’ 앞쪽 제거 → | 실제 내용"),
            Section("두 기준 사이", "두 기준 사이를 삭제하거나 그 사이 내용만 남길 수 있습니다. 시작·종료 기준의 유지 여부를 각각 정할 수 있고 기준이 없는 줄은 건너뜁니다.")),

        Topic("줄 꾸미기·분할·합치기", "반복되는 줄 단위 작업을 미리보기와 한 번의 Undo로 처리합니다.", ["글자 삭제", "접두사", "접미사", "분할", "합치기", "줄 번호"],
            Section("글자와 접두·접미", "각 줄의 왼쪽 또는 오른쪽에서 지정한 문자 수를 제거하거나 모든 줄 앞뒤에 문구를 붙입니다. 이모지와 조합 문자는 중간에서 잘리지 않습니다. 빈 줄 포함 여부를 선택할 수 있습니다."),
            Section("분할·합치기·번호", "구분자를 줄바꿈으로 바꾸거나 여러 줄을 입력한 구분자로 합칩니다. 시작 번호와 번호 뒤 구분자를 정해 번호를 붙이고, 도구가 붙인 형태의 번호를 다시 제거할 수 있습니다.")),

        Topic("포함 행 삭제와 빠른 정리", "행 삭제, 치환과 로그에 안전한 정리를 제공합니다.", ["행 삭제", "치환", "중복", "공백", "로그 정리", "ANSI"],
            Section("포함 행 삭제와 치환", "특정 문장이 포함된 행은 텍스트와 연결된 개행까지 통째로 삭제합니다. 부분 일치와 대소문자 옵션을 지원합니다. 일괄 치환은 바뀌기 전후를 미리 확인할 수 있습니다."),
            Section("빠른 정리", "중복 줄 제거, 빈 줄 제거, 연속 빈 줄 합치기, 앞뒤 공백 제거를 제공합니다. 로그 정리는 ANSI 제어 시퀀스와 표시 불가능한 제어문자, 줄 끝 공백을 없애고 연속 빈 줄만 하나로 줄입니다. 타임스탬프·로그 레벨·들여쓰기·순서는 바꾸지 않습니다.")),

        Topic("미리보기·바로 적용·Undo", "대량 변경은 먼저 확인하거나 즉시 적용할 수 있습니다.", ["미리보기", "적용", "Undo", "Redo"],
            Section("안전하게 확인하기", "미리보기는 줄별 변경 전후와 변경·건너뜀 통계를 보여줍니다. 변경할 항목이 없거나 원문이 수정되어 미리보기가 만료되면 ‘변경 적용’ 버튼은 비활성화됩니다."),
            Section("바로 적용", "‘바로 적용’은 별도 확인창 없이 현재 문서에 적용합니다. 미리보기 적용과 바로 적용 모두 하나의 작업으로 묶이므로 한 번의 Undo로 전체를 되돌릴 수 있습니다.", "Ctrl+Z  실행 취소\nCtrl+Y  다시 실행")),

        Topic("즐겨찾기·글꼴·테마·설정", "자주 쓰는 도구와 화면 설정은 다음 실행에도 유지됩니다.", ["즐겨찾기", "폰트", "맑은 고딕", "다크", "설정"],
            Section("즐겨찾기", "‘즐겨찾기 편집’에서 자주 쓰는 정리 기능을 고정하거나 해제합니다. 입력이 필요한 도구는 버튼을 누르면 해당 항목을 열고, 즉시 실행형 기능은 미리보기를 만듭니다."),
            Section("화면과 자동 저장", "설치된 Windows 글꼴을 검색해 선택할 수 있으며 맑은 고딕은 ‘맑은 고딕 (Malgun Gothic)’으로 표시됩니다. 테마·글꼴·크기·패널 상태·최근 검색·도구 입력값은 변경 후 자동 저장됩니다.")),

        Topic("Diff 작업 공간과 Merge 탭", "여러 비교 작업을 전용 창의 탭으로 동시에 열어 둡니다.", ["Diff", "Merge", "비교", "병합", "탭"],
            Section("작업 공간 열기", "상단 ‘비교/병합’을 누르면 좌우가 비어 있는 Diff 작업 공간이 바로 열립니다. + 버튼으로 Merge 탭을 추가하며 각 탭은 텍스트, Undo, 옵션, 스크롤과 현재 차이를 독립적으로 유지합니다."),
            Section("Merge 탭", "탭 제목에는 좌우 이름과 차이 개수가 표시되고 수정된 탭에는 점이 붙습니다. 탭을 닫을 때 수정한 내용의 저장·원본 반영·추출·버리기 여부를 선택합니다.", "Ctrl+W  현재 Merge 탭 닫기\nCtrl+Tab / Ctrl+Shift+Tab  Merge 탭 순환")),

        Topic("Diff 파일 드롭과 소스", "파일을 좌우에 놓거나 두 파일을 한 번에 놓아 즉시 비교합니다.", ["드래그", "드롭", "파일 두 개", "클립보드", "선택 영역"],
            Section("파일 놓기", "파일 하나를 원하는 좌우 영역에 놓으면 그쪽 소스로 설정됩니다. 파일 두 개를 작업 공간에 함께 놓으면 새 Merge 탭의 왼쪽과 오른쪽에 OS 전달 순서대로 배치합니다. 완전히 빈 탭이 있으면 그 탭을 재사용합니다."),
            Section("다른 소스", "각 영역의 소스 메뉴에서 파일, 열린 문서, 클립보드를 선택하거나 비울 수 있습니다. 현재 문서↔클립보드와 선택 영역↔클립보드 비교는 새 Merge 탭으로 열립니다. 폴더는 열지 않으며 세 개 이상을 놓으면 앞의 두 파일만 사용합니다.")),

        Topic("차이 탐색과 병합", "변경 블록을 이동하고 필요한 방향으로 복사합니다.", ["추가", "삭제", "수정", "스크롤 동기화", "블록 복사", "좌우 교환"],
            Section("차이 보기", "추가·삭제·수정 줄은 색과 여백 표시로 구분되고 수정 줄 안의 변경 부분도 강조됩니다. 이전·다음 버튼으로 블록을 이동하며 Scroll Sync가 켜지면 대응되는 좌우 줄이 함께 보입니다.", "Alt+↑ / Alt+↓  이전 / 다음 차이"),
            Section("병합과 복사", "가운데 화살표로 현재 블록을 반대쪽에 복사하거나 전체 방향 복사를 사용합니다. 대상이 읽기 전용이면 그 방향은 비활성화됩니다. 블록·전체 병합은 각각 한 번의 Undo로 복원됩니다.", "Alt+←  오른쪽 블록을 왼쪽으로\nAlt+→  왼쪽 블록을 오른쪽으로"),
            Section("좌우 교환과 선택 비교", "좌우 교환은 텍스트와 경로·인코딩·연결 상태를 함께 바꿉니다. Copy Left·Right·Both로 현재 차이를 복사할 수 있고, 양쪽 선택 영역만 새 읽기 전용 Merge 탭에서 다시 비교할 수도 있습니다.")),

        Topic("Diff 저장과 원본 반영", "Diff 편집 내용과 실제 문서·디스크 저장은 명시적으로 구분됩니다.", ["원본 반영", "stale", "외부 변경", "저장"],
            Section("열린 문서에 반영", "열린 문서에서 복사한 Diff 버퍼를 편집해도 원본 탭은 즉시 바뀌지 않습니다. ‘원본 탭에 반영’을 눌러 전체를 한 번의 Replace 작업으로 적용하며 디스크에는 아직 저장하지 않습니다."),
            Section("변경 충돌 보호", "비교를 연 뒤 원본 탭이 바뀌면 stale 안내가 나타나고 일반 반영이 차단됩니다. 최신 원본을 다시 불러오거나 손실 가능성 확인 후 현재 Diff 내용으로 교체할 수 있습니다. 직접 연 파일도 길이와 수정 시각이 달라지면 덮어쓰기 전에 경고합니다."),
            Section("닫기와 저장", "Merge 자체는 파일을 저장하지 않습니다. Ctrl+S는 포커스가 있는 쪽을 저장합니다. 클립보드·선택 영역은 다른 이름으로 저장하거나 새 문서로 추출할 수 있습니다. 종료에서 취소하면 작업 공간과 일반 문서가 모두 유지됩니다.")),

        Topic("키보드 단축키", "편집기와 Diff 작업 공간에서 자주 쓰는 명령을 키보드로 실행합니다.", ["키보드", "단축키", "F1"],
            Section("파일과 탭", "Ctrl+N 새 문서 · Ctrl+O 열기 · Ctrl+S 저장 · Ctrl+Shift+S 다른 이름으로 저장 · Ctrl+W/Ctrl+F4 현재 탭 닫기 · Ctrl+Shift+W 모두 닫기 · Ctrl+Tab/Shift+Ctrl+Tab 탭 순환"),
            Section("편집과 검색", "Ctrl+Z/Y 실행 취소·다시 실행 · Ctrl+X/C/V 잘라내기·복사·붙여넣기 · Ctrl+A 전체 선택 · Ctrl+F 검색 · Ctrl+H 치환 · F3/Shift+F3 다음·이전 결과 · F1 도움말"),
            Section("Diff / Merge", "Alt+↑/↓ 이전·다음 차이 · Alt+← 오른쪽에서 왼쪽 병합 · Alt+→ 왼쪽에서 오른쪽 병합 · Ctrl+S 포커스 쪽 저장 · Ctrl+W 현재 Merge 탭 닫기")),

        Topic("알려진 제약", "v1 범위에서 지원하는 텍스트 편집과 비교의 경계를 설명합니다.", ["제약", "정규식", "3-way", "플러그인"],
            Section("일반 편집기", "초대용량 로그 스트리밍 전용 모드, 정규식 직접 입력, 코드 구문 강조, 플러그인과 클라우드 동기화는 현재 제공하지 않습니다."),
            Section("Diff / Merge", "Overview heatmap, 동일 영역 접기, 문자열 일부만 반영하는 Smart Merge, Merge Basket, Bookmark·Memo, 사용자 지정 Ignore Regex, Snapshot·Timeline과 실제 3-way Merge는 후속 범위입니다."))
    ];

    private static HelpTopic Topic(string title, string summary, IReadOnlyList<string> keywords, params HelpSection[] sections) =>
        new(title, summary, keywords, sections);

    private static HelpSection Section(string heading, string body, string? example = null) => new(heading, body, example);
}
