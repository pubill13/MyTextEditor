# MyTextEditor 작업 인수인계

최종 갱신: 2026-09-10 (Asia/Seoul)

## 프로젝트의 현재 목표

MyTextEditor는 복잡한 정규식 없이 줄 검색·추출·삭제·가공을 수행하는 Windows 10/11 x64용 텍스트 편집기다. C#·WPF·.NET 9, Scintilla5.NET과 DiffPlex를 사용한다. 30MB·약 3천만 자 로그를 2초 이내에 열고 .NET 설치 없이 실행되는 단일 EXE를 제공하는 기준을 유지한다.

현재 버전 목표는 `1.6.0`이다. 앱당 하나의 모델리스 Diff 작업 공간에서 여러 Merge 탭을 독립적으로 유지하고, 파일을 좌우에 직접 드롭해 바로 비교할 수 있게 한다. 기존 단문 도움말은 검색과 목차를 갖춘 전용 창으로 교체한다.

## 지금까지 완료된 작업

- v1.4까지 대용량 Scintilla 편집기, 다중 문서 탭, 파일 드롭, 단순 조건 검색, 검색 결과 탭, 텍스트 변환·바로 적용, 로그 정리, 즐겨찾기, 최근 검색, 테마·글꼴·설정 자동 저장을 구현했다.
- v1.5에서 UI 독립 `TextDiffEngine`·`TextMergeService`, 줄·인라인 차이 강조, 탐색, 블록/전체 양방향 병합, 좌우 교환, 차이 복사, 스크롤 동기화, 원본 revision과 외부 파일 변경 보호를 구현했다.
- v1.6에서 대상 선택 대화상자와 여러 `DiffWindow`를 제거하고 단일 `DiffWorkspaceWindow`와 여러 `DiffTabView`로 바꿨다.
- `비교/병합`은 좌우가 비어 있는 탭을 즉시 열거나 기존 빈 탭을 선택한다. 각 Merge 탭은 endpoint, 편집기, Undo, 비교 결과, 옵션, generation과 스크롤을 독립적으로 보존한다.
- Merge 탭 추가·개별 닫기·다른 비교 닫기·모두 닫기, `Ctrl+W`/`Ctrl+F4`, `Ctrl+Tab`/`Ctrl+Shift+Tab`을 지원한다.
- 단일 파일은 놓은 좌우 영역 또는 현재 탭의 첫 빈 영역에 넣는다. 두 파일은 둘 다 로드에 성공한 뒤 새 탭 좌우에 OS 전달 순서로 배치하며 완전히 빈 탭이 있으면 재사용한다. 세 개 이상, 폴더와 실패 파일은 제외 사실을 한 번 알린다.
- 실제 내용이 빈 파일과 미지정 영역을 `DiffEndpointKind.Empty`와 `IsReady`로 구분한다. 양쪽 준비 전에는 비교·탐색·병합을 비활성화한다.
- 열린 문서와 클립보드 빠른 비교, 선택 영역과 클립보드 비교, 양쪽 선택 영역 비교는 같은 작업 공간에 새 Merge 탭을 만든다.
- 검색 가능한 모델리스 `HelpWindow`와 15개 도움말 주제를 추가했다. 파일·검색·모든 정리 도구·설정·Diff/Merge·저장·단축키·제약을 설명하며 `F1`, 도움말 안 `Ctrl+F`, 검색 결과 없음 초기화를 지원한다.
- 도움말 창과 Diff 작업 공간의 크기·위치, 새 Merge 탭의 기본 비교 옵션을 설정에 저장한다. 도움말 검색어와 Merge 탭 내용은 저장하지 않는다.
- 앱·파일 버전을 `1.6.0`으로 올렸다.

## 현재 구현/수정 중인 작업

v1.6 기능 구현과 로컬 검증이 완료된 상태다. `main` 커밋, `v1.6` 태그와 GitHub Release 게시가 이 문서 갱신 뒤 이어진다. Release에는 자체 포함 `MyTextEditor.exe`와 `MyTextEditor-win-x64.zip`을 첨부한다.

## 주요 설계 결정과 이유

- 작업 공간 창은 하나만 두고 비교 한 건의 책임은 `DiffTabView`가 가진다. 공통 창 수명과 탭 명령을 비교 상태에서 분리하면서 비활성 탭의 네이티브 편집기와 Undo를 유지하기 위해서다.
- 빈 endpoint를 빈 문자열 파일과 구분한다. 내용이 0바이트인 파일도 정상 비교 소스여야 하기 때문이다.
- 두 파일 드롭은 두 endpoint 로드를 먼저 끝낸 뒤 탭에 반영한다. 한쪽 로드 실패로 불완전한 비교가 남지 않게 한다.
- 일반 편집기와 Diff 작업 공간의 드롭 처리를 분리한다. 일반 창의 기존 다중 문서 열기 동작을 바꾸지 않는다.
- DiffPlex 모델은 Core 자체 도메인 모델로 변환하며 WPF/Scintilla 타입을 Core에 넣지 않는다.
- 병합은 정규화 문자열이 아니라 원문에 적용하고 대상 줄바꿈과 끝 개행을 보존한다. 각 블록·전체 병합은 Scintilla `ReplaceAll` 한 번으로 적용해 Undo 한 단계로 되돌린다.
- 직접 편집 뒤 300ms 비동기 비교를 실행하고 generation이 지난 결과는 버린다. UI를 막지 않고 오래된 결과를 적용하지 않기 위해서다.
- 열린 문서는 시작 revision을 보관하며 원본 반영 직전에 다시 검사한다. Diff 버퍼가 최신 원본을 조용히 덮지 못하게 한다.
- 도움말은 외부 Markdown/UI 라이브러리 없이 작은 `HelpTopic` 모델과 WPF 기본 컨트롤로 구성했다. 현재 규모에서 검색 가능한 정적 안내에 충분하다.
- 앱 종료는 Diff 탭의 종료 가능 여부를 모두 확인한 뒤 일반 문서를 확인한다. 어느 단계에서 취소해도 Merge 탭을 제거하지 않고 이전 종료 승인을 초기화한다.

## 사용자가 명시한 요구사항 및 변경사항

- 비교/병합 버튼은 대상 선택창 없이 빈 좌우 비교 화면을 즉시 열어야 한다.
- Diff는 앱당 모델리스 창 하나와 여러 Merge 탭으로 구성하며 탭별 상태를 잃지 않아야 한다.
- 파일 두 개를 함께 드롭하면 새 Merge 탭 좌우에 자동 배치하고, 완전히 빈 초기 탭은 재사용해야 한다.
- 파일 세 개 이상은 앞의 두 개만 사용한다. 폴더를 재귀 탐색하지 않는다.
- 일반 편집기 상태와 일반 창의 파일 드롭은 기존처럼 문서 탭을 열어야 한다.
- 좌우 각각 파일 선택, 열린 문서, 클립보드, 비우기 소스를 제공해야 한다.
- 선택 영역 비교와 클립보드 빠른 비교도 작업 공간의 새 탭으로 열어야 한다.
- Merge 탭과 비교 이력은 재실행 후 복원하지 않는다.
- 기능 수에 맞는 전용 도움말, 목차, 검색, 예시, 주의사항과 전체 단축키 안내를 제공해야 한다.
- 기존 대용량 로딩, 검색, 변환, 인코딩, 테마, 설정 저장과 종료 성능을 회귀시키지 않는다.

## 수정한 주요 파일과 역할

| 파일 | 역할 |
| --- | --- |
| `src/MyTextEditor/Diff/DiffContracts.cs` | Empty endpoint, readiness, 원본 문서 ID와 작업 공간 callback 계약 |
| `src/MyTextEditor/Diff/DiffTabView.xaml(.cs)` | 비교 한 건의 좌우 편집기·옵션·탐색·병합·소스·드롭·저장·stale 처리 |
| `src/MyTextEditor/Diff/DiffWorkspaceWindow.xaml(.cs)` | 단일 모델리스 창, Merge 탭 생성·순환·닫기, 두 파일 드롭, 공통 수명 |
| `src/MyTextEditor/MainWindow.Diff.cs` | 작업 공간 singleton, 빠른 비교, 열린 문서 callback, 설정·테마·종료 연결 |
| `src/MyTextEditor/Controls/ScintillaEditorHost.cs` | F1과 Merge 탭 단축키의 child HWND 전달 |
| `src/MyTextEditor/Help/HelpTopic.cs`, `HelpCatalog.cs` | 검색 가능한 도움말 데이터 모델과 15개 주제 |
| `src/MyTextEditor/Help/HelpWindow.xaml(.cs)` | 모델리스 도움말 목차·검색·본문·결과 없음 UI |
| `src/MyTextEditor/MainWindow.Help.cs` | 도움말 창 singleton과 배치 저장 |
| `src/MyTextEditor/Models/UserSettings.cs`, `Services/SettingsService.cs` | 도움말 배치와 Diff 기본 옵션의 호환 저장·정규화 |
| `tests/MyTextEditor.Performance/Program.cs` | Scintilla 단축키, 탭형 Diff, 도움말 검색, 30MB 회귀 실행 검증 |
| `README.md` | v1.6 작업 공간·드롭·도움말 사용법 |

삭제한 `DiffSourceDialog.xaml(.cs)`와 `DiffWindow.xaml(.cs)`는 v1.5 단일 비교 창 구현이며 다시 사용하지 않는다.

## 검증 결과

- Core 테스트: 30/30 통과
- 50,000줄·100개 변경 Diff: 약 50ms, 3초 기준 통과
- Release 솔루션 빌드: 경고 0, 오류 0
- Scintilla 단축키 전달, 탭형 Diff 비교·단일 Undo, 빈 탭 재사용, 빈 파일 readiness, 두 파일 드롭, 도움말 검색 실행 검증: 통과
- 30,000,044바이트 로그 3회 로딩 중앙값: 약 0.236초, 2초 기준 통과
- 30MB Scintilla 해제: 약 0.021~0.057초, 1.5초 기준 통과
- 자체 포함 EXE 시작·종료는 Release 게시 전에 다시 검증한다.

## 알려진 문제/미해결 이슈

- 100%·125%·150% DPI와 라이트·다크 테마는 XAML 빌드와 프로그램 실행으로 검증하지만 실제 여러 모니터에서의 육안 평가는 사용 환경에서 한 번 더 확인해야 한다.
- 저장·버리기·취소 MessageBox의 모든 조합을 자동 클릭하는 UI 자동화는 없다. 종료 전에 탭을 제거하지 않는 구조와 Core/host 실행 검증으로 보완한다.
- 삭제 전용 블록 반대편에 가상 빈 행을 삽입하지 않는다. 대응 줄 anchor 보간으로 스크롤을 맞춘다.
- Diff 파일은 전체 문자열로 로드한다. 현재 5만 줄 성능 목표는 만족하지만 수백 MB 파일 두 개의 동시 비교는 후속 최적화 대상이다.
- Heatmap, 동일 영역 접기, Smart Merge, Merge Basket, custom ignore regex, snapshot/timeline, 실제 3-way Merge는 후속 범위다.

## 다음에 해야 할 작업과 우선순위

1. 실제 사무실 데이터로 파일 한 개 좌우 드롭, 두 파일 동시 드롭과 여러 Merge 탭 전환을 확인한다.
2. 100%·125%·150% DPI 및 라이트·다크 테마에서 탭 헤더, 드롭 안내, gutter와 도움말 레이아웃을 육안 확인한다.
3. 큰 추가·삭제 블록에서 anchor 스크롤 감각과 가운데 병합 버튼 위치를 조정한다.
4. 실제 사용 피드백에 따라 후속 Diff 기능의 우선순위를 정한다.

## 다음 Agent가 반드시 알아야 할 주의사항

- 루트 `AGENTS.md`를 먼저 읽고 사용자 변경을 reset·checkout·revert하지 않는다.
- 앱당 `DiffWorkspaceWindow`는 하나다. 새 비교 요구를 별도 창으로 열지 말고 `OpenComparison`으로 탭을 추가한다.
- 탭 전환 때 endpoint나 Scintilla host를 다시 만들지 않는다. 리소스는 탭을 실제로 닫을 때 한 번만 해제한다.
- 두 파일 드롭은 양쪽 로드 성공 전 기존 탭을 수정하거나 새 탭을 남기지 않는다.
- `DiffEndpointKind.Empty`와 내용이 빈 File/Clipboard endpoint를 혼동하지 않는다.
- 열린 문서 snapshot에는 반드시 `SourceDocumentId`와 revision을 함께 넣는다.
- 원본 반영은 revision을 재검사하고 `ReplaceAll` 한 번으로 적용하며 자동 저장하지 않는다.
- `ApplyBlock` 뒤 이전 `DiffBlock` 좌표를 재사용하지 말고 전체 Diff를 다시 계산한다.
- Scintilla inline 강조 위치에 UTF-8 바이트 길이를 직접 쓰지 말고 `GetLineTextRange`로 UTF-16 범위를 변환한다.
- 일반 창 드롭은 문서 열기이고 Diff 자동 탭 생성은 작업 공간 내부 드롭만 해당한다.
- 앱 종료의 일반 문서 확인이 취소되면 `CancelPreparedClose()`로 Diff 종료 승인을 초기화한다.
- `bin/`, `obj/`, `artifacts/`, 사용자 `settings.json`은 커밋하지 않는다. 배포 파일은 GitHub Release에만 첨부한다.
- 변경 뒤 Release 빌드, Core 테스트, 성능 실행기와 자체 포함 EXE 시작·종료를 다시 검증한다.
