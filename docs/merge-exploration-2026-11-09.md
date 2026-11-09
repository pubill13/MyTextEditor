# 비교·병합 탐색 검증 — 2026-11-09

이 문서는 수정 전 탐색 결과다. 발견한 문제의 후속 수정과 최종 검증은 [비교·병합 문제 개선](merge-improvements-2026-11-09.md)에 기록했다.

대상: OmniEdit 1.11.11, 커밋 `40bda90a8e92f1cf9d1ba6586b22141de4f52d9f`의 제품 코드. 제품 코드는 수정하지 않았다. 목적은 다양한 사용자 흐름을 실행하고 결함과 불편을 발굴하는 것이다.

## 결과

신규 **91개 시나리오 관측: 79개 기대 충족, 12개 기대 미충족**. 같은 문제를 독립 경로에서 재현한 사례가 포함된다. 미충족 11개는 아래 **6개 결함과 1개 UX 개선 항목**으로 묶었다. 나머지 1개(`S-enter`)인 치환 입력란의 Enter 동작은 별도의 UX 정책 관측이며 버튼 Enter 결함과 구분한다. 모든 실패가 서로 다른 버그라는 의미는 아니다.

| 검증 묶음 | 사례 | 충족 | 미충족 |
|---|---:|---:|---:|
| 병합·검색·키보드·저장·화면·탭 | 70 | 63 | 7 |
| 원본 재로드·클립보드 메타·겹친 저장 | 6 | 3 | 3 |
| 실제 메인 창 문서와 연결한 전체 흐름 | 15 | 13 | 2 |
| 합계 | 91 | 79 | 12 |

Release 빌드: 경고 0, 오류 0. 기존 `--merge-scroll`, `--merge-lifetime`, `--omni-merge`, `--merge-clarity`, `--merge-110` 5묶음도 모두 통과했다. 기존 테스트가 통과해도 아래 사용자 흐름의 결함은 남는다.

최종 관측과 원본 실행 결과: [최종 JSON](../artifacts/merge-exploration/final-results.json), [전체 70개 실행](../artifacts/merge-exploration/results.json), [K01 재검증](../artifacts/merge-exploration/focused-results.json), [원본/저장](../artifacts/merge-exploration/source-results.json), [재로드 후 병합 추가 검증](../artifacts/merge-exploration/filtered-source-results.json), [메인 창 연결 흐름](../artifacts/merge-exploration/workflow-results.json), [기존 회귀 묶음](../artifacts/merge-exploration/baseline-results.json).

## 우선 수정할 결함

### 1. 원본 다시 불러오기 후 오래된 비교 결과로 잘못 병합할 수 있음 — 높음

재현:

1. 열린 문서를 왼쪽 소스로 연결한다. 처음 내용은 `head\nold source\ntail`이다.
2. 오른쪽에는 `head\nnew line 1\nnew line 2\ntail`을 둔다.
3. 원본 문서를 오른쪽과 같은 내용으로 편집하고 **원본 다시 불러오기**를 실행한다.
4. 양쪽 편집기의 실제 본문은 같아지지만 차이 1개가 그대로 남는다.
5. 남은 오른쪽→왼쪽 병합 명령을 실행하면 왼쪽에 `new line 2`가 중복된다.

관측한 왼쪽 결과:

```text
head
new line 1
new line 2
new line 2
tail
```

`SOURCE-RELOAD-CACHE`, `SOURCE-RELOAD-MERGE-SAFETY`, `WF-06-REAL-LINKED-RELOAD-CACHE`로 재현했다. 실제 MainWindow 원본 콜백에서도 재로드 후 차이가 남는 현상을 확인했다. 잘못된 병합은 화살표와 같은 `MergeCurrent` 명령을 호출해 검증했으며 물리 화살표 클릭으로 보고하지 않는다. 디스크 자동 저장이나 영구 손실을 입증한 것은 아니다.

원인 근거: [ReloadSource](../src/MyTextEditor/Diff/DiffTabView.xaml.cs:797)는 본문을 다시 로드하면서 스냅샷 캐시를 지우지 않는다. [LoadUtf8](../src/MyTextEditor/Controls/ScintillaEditorHost.cs:287)는 revision을 0으로 되돌린다. [CaptureSideAsync](../src/MyTextEditor/Diff/DiffTabView.Comparison110.cs:27)는 revision이 같은 이전 캐시를 재사용한다.

개선: 재로드 시 본문·인덱스·표시 결과 캐시를 무효화하고 최신 내용의 비교가 끝날 때까지 병합을 막아야 한다.

### 2. Ctrl+Tab 뒤 입력이 보이지 않는 이전 비교 탭을 수정함 — 높음

재현:

1. 비교 탭 A와 B를 만들고 B의 왼쪽 편집기를 클릭한다.
2. Ctrl+Tab으로 A를 화면에 띄운다.
3. `x`를 입력한다.

관측: A는 `visible A` 그대로인데 숨겨진 B가 `previous Bx`로 바뀌었다. A의 native focus는 false, B는 true였다. 두 번째 Ctrl+Tab도 동작하지 않았다. 현재 A 편집기를 클릭한 뒤에는 Ctrl+Tab이 다시 동작했다.

근거: `T-tab-type`, `T01`. 실제 마우스 클릭과 가상 키 입력을 사용했다. [화면](../artifacts/merge-exploration/typing-after-tab-switch.png). [CycleTab](../src/MyTextEditor/Diff/DiffWorkspaceWindow.xaml.cs:326)은 선택 인덱스만 바꾸며 새 탭의 native editor로 포커스를 옮기지 않는다.

개선: 탭 전환 시 이전 native 포커스를 해제하고 새 탭의 적절한 편집기로 포커스를 옮겨야 한다. 입력이 비활성 탭으로 전달되지 않는 검증도 필요하다.

### 3. 좌우 교환 시 잠금 상태가 오염됨 — 높음

| 교환 전 원래 문서 잠금 | 교환 후 원래 문서 잠금 | 관측 |
|---|---|---|
| L 잠김 / R 풀림 | L 풀림 / R 풀림 | 보호한 문서가 의도 없이 잠금 해제됨 |
| L 풀림 / R 잠김 | L 잠김 / R 잠김 | 편집 가능한 문서까지 잠김 |
| 둘 다 풀림 | 둘 다 풀림 | 정상 |
| 둘 다 잠김 | 둘 다 잠김 | 정상 |

근거: `T-lock-swap-True-False`, `T-lock-swap-False-True` 및 나머지 두 대조군. 실제 좌우 교환 버튼을 클릭했다. **Undo 이력 유실은 아니다.** 수동으로 잠금을 해제하면 Undo/Redo가 작동했고, 양쪽 잠금이 풀린 대조군의 교환에서도 Undo가 유지됐다.

원인 근거: [Swap_Click](../src/MyTextEditor/Diff/DiffTabView.xaml.cs:705) 뒤 [RefreshEndpointHeaders](../src/MyTextEditor/Diff/DiffTabView.xaml.cs:681)가 체크박스를 순서대로 바꾼다. 중간에 [ReadOnly_Changed](../src/MyTextEditor/Diff/DiffTabView.xaml.cs:670)가 아직 갱신되지 않은 반대 체크박스 값까지 양쪽 모델에 반영한다.

개선: 교환 중 UI 상태 반영으로 변경 이벤트가 모델을 다시 덮어쓰지 않도록 해야 한다.

### 4. 치환 버튼에 포커스를 두고 Enter를 눌러도 치환되지 않음 — 중간

Ctrl+H → 찾기 `cat`, 바꾸기 `dog` → 일치 항목 선택 → **바꾸기 버튼에 키보드 포커스** → Enter. 버튼을 실행하지 않고 다음 찾기로 처리하며 본문은 `cat cat` 그대로다.

근거: `S-button-enter`, [화면](../artifacts/merge-exploration/replacement-button-enter.png). 반면 마우스로 모두 바꾸기를 클릭하면 치환되며 Ctrl+Z/Redo도 정상이다(`K01`). [FindBar_PreviewKeyDown](../src/MyTextEditor/Diff/DiffTabView.Search.cs:106)이 자식 컨트롤 종류와 무관하게 Enter를 가로챈다.

개선: 검색 입력란의 Enter와 포커스된 버튼의 Enter 실행을 구분해야 한다. 바꿀 텍스트 입력란에서도 Enter가 다음 찾기를 실행하는 현상(`S-enter`)은 별도의 UX 선택 문제다. 입력란 Enter를 치환으로 바꿀지는 정책 결정이 필요하지만 버튼의 표준 실행까지 막는 것은 결함이다.

### 5. 동일 내용 전체 복사가 문서를 수정 상태로 만듦 — 낮음

양쪽에 같은 문자열을 두고 **복사 → 왼쪽 전체 → 오른쪽**을 실행한다. 본문은 같지만 수정 표시가 생기고 Undo 항목이 추가된다. 이후 닫을 때 불필요한 저장 확인을 유발할 수 있다.

근거: `WF-15-IDENTICAL-COPY-DIRTY`: 복사 전 Undo=false, 복사 후 Undo=true, native modified=true, view unsaved=true. 실제 복사 메뉴의 Routed Click을 실행했다.

원인 근거: [CopyAll](../src/MyTextEditor/Diff/DiffTabView.xaml.cs:385)과 [ReplaceMergedSide](../src/MyTextEditor/Diff/DiffTabView.xaml.cs:393)가 결과가 같은 경우에도 전체 교체와 dirty 설정을 실행한다. 개선: 텍스트가 같으면 변경하지 않아야 한다.

### 6. 클립보드 잠금 해제 후에도 “읽기 전용”으로 안내함 — 낮음

클립보드 소스의 잠금을 풀고 실제로 본문을 편집해도 메타 설명은 `클립보드 스냅샷 · 읽기 전용`으로 남는다.

근거: `CLIPBOARD-LOCK-METADATA`, native readOnly=false이며 본문 변경을 확인했다. [DescribeEndpoint](../src/MyTextEditor/Diff/DiffTabView.xaml.cs:695)가 문구를 고정한다. 개선: 현재 잠금 상태를 반영하거나 소스 종류만 설명해야 한다.

## UX 개선 항목

**새 비교의 한쪽만 채운 뒤 비어 있는 반대쪽으로 전체 복사가 불가능하다.** 반대쪽을 건드리지 않은 Empty 상태에서는 준비가 안 됐다고 판단하여 전체 복사가 비활성화된다. `U-empty-target`, [화면](../artifacts/merge-exploration/empty-target-copy.png). 사용자가 한쪽을 시작점으로 삼아 복사한 뒤 일부만 바꾸려는 흐름이 막힌다. 빈 문자열을 가진 준비된 문서와 아직 소스를 고르지 않은 영역은 동작이 다르다. 전체 복사로 빈 영역을 새 편집 버퍼로 만드는 흐름을 지원할 가치가 있다.

## 정상 동작을 확인한 범위

- 16종 입력을 양방향으로 병합한 32개 사례: 양쪽 빈 문서, 한쪽 빈 문서, 동일 내용, 교체, 앞/뒤 삽입, 빈 줄 삭제, 떨어진 여러 차이, 한글·이모지·결합 문자, LF/CRLF, 마지막 개행, 탭/공백, 반복 줄, 2만 자 긴 줄, 전체 차이. 각 변경의 정확한 Undo/Redo와 최종 본문을 검사했다.
- 공백량·대소문자·빈 줄 무시 옵션의 결과 변화와 원문 보존.
- 실제 Ctrl+H, 마우스 치환, Ctrl+Z/Ctrl+Y, F7/F8, Alt+→ 병합, 잠금 중 입력·병합·치환 차단과 잠금 해제 후 입력.
- 부분 문자열·완전한 단어·대소문자·한글·이모지·삭제·미일치·같은 값 치환의 대상 독립성과 Undo.
- UTF-8, UTF-8 BOM, UTF-16 LE/BE BOM, CP949 각각의 LF/CRLF 불러오기→편집→저장→재로드 10개 조합.
- 메인 창에서 비교 창 진입·숨김·복원, 사본 편집 격리, 원본 반영과 원본 Undo, stale 원본 반영 거절, 새 문서 추출, 닫기 취소/버리기.
- 파일 1/2/3개 전달 처리, 입력 순서와 제외 안내, 누락 파일이 포함된 쌍 실패 시 기존 비교 보존, 외부 파일 변경 후 저장 취소 시 디스크와 편집 내용 보존.
- 64MiB 저장 뒤 추가 편집과 작은 내용 저장을 겹친 3회에서 최종 디스크와 편집기가 최신 내용으로 일치했다. 이 환경에서 관측한 결과이며 모든 저장 순서의 안전성을 증명하지는 않는다.
- 900×560, 1200×760, 1600×900에서 찾기·치환 버튼과 편집 영역을 확인하고 캡처를 검토했다. 기존 레이아웃 검증은 5개 테마의 최소/큰 창을 통과했다. [최소 창](../artifacts/merge-exploration/layout-900.png), [큰 창](../artifacts/merge-exploration/layout-1600.png).

## 검증 방법과 한계

실제 WPF 창과 native Scintilla 편집기를 실행했다. 키보드·마우스 사례는 `keybd_event`/`mouse_event`로 조작했다. 조합을 넓게 검사한 병합 사례는 private 명령을 reflection으로 실행했다. 메인 창 연결 사례는 실제 MainWindow 콜백을 사용하되 Routed Click과 공개 처리 API를 함께 사용했다. 모든 사례를 수작업 또는 배포 EXE 조작으로 검증했다는 뜻은 아니다.

파일 드롭은 실제 Explorer 드래그 대신 파일 전달 처리 경로를 호출했다. 실제 파일 선택/다른 이름 저장 대화상자의 전체 조작, 한국어 IME 조합, 혼합 DPI, 300MB 전체 비교는 이번에 검증하지 않았다. 사용자 원본 파일은 사용하지 않았고 임시 fixture와 격리된 설정으로 검사했다.

초기 검증기의 공백 옵션 기대값을 `ab`에서 `a b`로 바로잡았다. 해당 옵션은 공백량 차이를 정규화하며 단어 사이 구분까지 없애는 옵션이 아니다. 초기 `SendKeys` 입력과 프로그램 방식의 초기 포커스 때문에 생긴 실패는 실제 클릭·가상 키 입력으로 재검증하여 제품 결함에서 제외했다. 원본 관측 JSON은 보존하고 최종 JSON에는 최신 관측을 반영했다.

## 재실행

```powershell
dotnet build MyTextEditor.sln -c Release --no-restore -p:NuGetAudit=false
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --merge-exploration
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --merge-source-exploration
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --merge-workflow-exploration
```

특정 사례만 실행하려면 `--cases=T-tab-type` 또는 `--cases=SOURCE-RELOAD-CACHE`를 추가한다. 사례가 기대를 충족하지 않으면 종료 코드 1을 반환하고 나머지 관측을 계속 남긴다. 현재 제품 결함을 발굴하는 탐색 검증이므로 이 명령들이 모두 통과한다고 보고하지 않는다. 제품 수정·커밋·추가 배포는 수행하지 않았다.
