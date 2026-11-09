namespace MyTextEditor;

public partial class MainWindow
{
    private static readonly (string Description, string Example)[] TransformDescriptions =
    [
        ("각 줄의 첫 기준 앞을 삭제합니다. 기준이 없는 줄은 유지합니다. 기준 자체는 ‘시작 기준 유지’로 선택합니다.", "예시 · 기준 |, 기준 유지 켬\nINFO | 내용 → | 내용"),
        ("각 줄의 첫 기준 뒤를 삭제합니다. 기준이 없는 줄은 유지합니다. 기준 자체는 ‘시작 기준 유지’로 선택합니다.", "예시 · 기준 |, 기준 유지 켬\n내용 | 부가정보 → 내용 |"),
        ("첫 시작 기준과 그 뒤 첫 종료 기준 사이를 삭제합니다. 두 기준이 순서대로 없는 줄은 유지합니다. 기준 유지 여부는 각각 선택합니다.", "예시 · 기준 [ 와 ], 둘 다 유지\n앞[내용]뒤 → 앞[]뒤"),
        ("첫 시작 기준과 그 뒤 첫 종료 기준 사이만 남깁니다. 두 기준이 순서대로 없는 줄은 유지합니다. 기준 유지 여부는 각각 선택합니다.", "예시 · 기준 [ 와 ], 둘 다 유지\n앞[내용]뒤 → [내용]"),
        ("각 줄 왼쪽에서 지정한 글자 수를 삭제합니다. 0이면 그대로, 줄보다 많으면 빈 줄이 됩니다. 이모지·결합 문자는 중간에서 자르지 않습니다.", "예시 · 2글자 삭제\n가😀나 → 나"),
        ("각 줄 오른쪽에서 지정한 글자 수를 삭제합니다. 0이면 그대로, 줄보다 많으면 빈 줄이 됩니다. 이모지·결합 문자는 중간에서 자르지 않습니다.", "예시 · 2글자 삭제\n가😀나 → 가"),
        ("각 줄 앞에 입력한 텍스트를 그대로 붙입니다. ‘빈 줄에도 적용’을 끄면 공백·탭만 있는 줄도 건너뜁니다.", "예시 · 앞에 > 추가\n내용 → >내용"),
        ("각 줄 뒤에 입력한 텍스트를 그대로 붙입니다. ‘빈 줄에도 적용’을 끄면 공백·탭만 있는 줄도 건너뜁니다.", "예시 · 뒤에 ; 추가\n내용 → 내용;"),
        ("입력한 구분자를 모두 줄바꿈으로 바꿉니다. 연속 구분자는 빈 줄을 만들고 각 항목의 공백은 유지합니다. 구분자는 비울 수 없습니다.", "예시 · 구분자 ,\nA,,B → A ↵ 빈 줄 ↵ B"),
        ("현재 문서의 줄바꿈을 입력한 구분자로 바꿔 한 줄로 합칩니다. 구분자를 비우면 바로 이어 붙입니다. 빈 줄·끝 개행 자리에도 구분자가 들어갑니다.", "예시 · 구분자 ,\nA ↵ B → A,B"),
        ("각 줄 앞에 시작 번호부터 연속 번호와 구분자를 붙입니다. 빈 줄을 제외하면 공백·탭만 있는 줄은 건너뛰며 번호도 사용하지 않습니다.", "예시 · 시작 1, 구분자 : (뒤 공백)\n내용 → 1: 내용"),
        ("줄 앞의 숫자와 입력한 구분자를 삭제합니다. 이 도구가 붙였는지는 구별하지 않아 원래 본문의 번호도 지울 수 있습니다. 맞지 않는 줄은 유지합니다.", "예시 · 구분자 : (뒤 공백)\n123: 내용 → 내용")
    ];

    private void UpdateTransformDescription()
    {
        if (TrimOperationCombo is null) return;
        var index = TrimOperationCombo.SelectedIndex;
        if (index < 0 || index >= TransformDescriptions.Length) return;
        SetTransformTip(TrimOperationCombo, TransformDescriptions[index].Description + "\n" + TransformDescriptions[index].Example);
    }
    private static string QuickTransformTitle(string operation) => operation switch
    {
        "Duplicate" => "중복 줄 제거",
        "Blank" => "빈 줄 모두 제거",
        "Collapse" => "연속 빈 줄을 한 줄로",
        "Whitespace" => "줄 공백 정리",
        _ => "빠른 정리"
    };
}
