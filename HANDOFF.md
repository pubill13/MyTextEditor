# MyTextEditor 작업 인수인계

최종 갱신: 2026-09-09 (Asia/Seoul)

## 프로젝트의 현재 목표

MyTextEditor는 복잡한 정규식 없이 줄 검색·추출·삭제·가공을 수행하는 Windows 10/11 x64용 텍스트 편집기다. C#·WPF·.NET 9와 Scintilla5.NET을 사용하며, 30MB·약 3천만 자 로그를 2초 이내에 여는 성능과 .NET 설치가 필요 없는 단일 EXE 배포를 유지한다.

현재 목표는 `1.5.0`이다. 기존 문서 탭을 직접 변경하지 않는 모델리스 Diff / Merge 창에서 파일, 열린 문서, 클립보드, 선택 영역을 비교하고 필요한 차이만 양방향으로 병합한다.

## 지금까지 완료된 작업

- v1.4까지 대용량 Scintilla 편집기, 다중 탭, 파일 드롭, 단순 조건 검색, 검색 결과별 탭과 snapshot, 텍스트 변환·바로 적용, 로그 정리, 즐겨찾기, 최근 검색, 테마·글꼴·설정 자동 저장을 구현했다.
- Core에 DiffPlex 1.9.0과 UI 독립 `TextDiffEngine`·`TextMergeService`·Diff 도메인 모델을 추가했다.
- CRLF/LF/CR 자체 차이는 무시하고 끝 개행 차이는 별도 블록으로 보존한다. 공백·대소문자·빈 줄 무시 옵션에도 원본 줄 번호와 원문을 유지한다.
- 파일, 열린 문서, 클립보드, 현재 선택 영역을 좌우 endpoint로 선택하는 창을 추가했다.
- 별도 모델리스 Diff 창에 좌우 Scintilla 편집기, 줄·인라인 강조, 차이 통계·탐색, 블록/전체 양방향 병합, 읽기 전용, 좌우 교환, 차이 복사를 구현했다.
- 직접 편집 뒤 300ms 비동기 재계산, generation 기반 이전 결과 폐기, 재계산 대기 중 이전 블록 무효화를 구현했다.
- anchor 보간 방식 스크롤 동기화와 Scintilla 포커스에서 동작하는 Alt 방향키·Ctrl+S·Ctrl+Z/Y 처리를 구현했다.
- 열린 문서는 사본으로 비교하고 명시적 `원본 반영` 때만 원본 탭에 ReplaceAll 한 번으로 적용한다. 원본 revision이 달라지면 반영을 막고 최신 원본 재로딩 또는 확인 후 강제 교체를 제공한다.
- 직접 연 파일의 길이·최종 수정 시각을 확인해 외부 변경을 경고한다. 클립보드·선택 영역은 다른 이름 저장과 새 문서 추출을 제공한다.
- 메인 창이 모든 Diff 창과 선택 영역 자식 창을 추적한다. 앱 종료 시 Diff 변경 처리를 먼저 묻고 어느 단계에서든 취소하면 창과 문서를 유지한다.
- Diff 옵션, 스크롤 동기화, 창 위치·크기를 자동 저장하고 실행 중 테마·글꼴 변경을 열려 있는 Diff 창에 전달한다.
- 앱과 파일 버전을 1.5.0으로 올리고 DiffPlex Apache-2.0 고지를 추가했다.

## 현재 구현/수정 중인 작업

v1.5 구현, 검토, 로컬 검증과 배포가 완료됐다. 구현 커밋은 `1c9f3dc`, 태그는 `v1.5`이며 GitHub Release는 `https://github.com/pubill13/MyTextEditor/releases/tag/v1.5`다. Release에 자체 포함 `MyTextEditor.exe`와 문서가 포함된 `MyTextEditor-win-x64.zip`을 첨부했다.

## 주요 설계 결정과 이유

- Diff 창은 모델리스 별도 창이다. 비교 편집이 기존 문서 탭을 암묵적으로 바꾸지 않고 여러 비교를 병행할 수 있다.
- DiffPlex 형식을 UI에 노출하지 않고 Core 자체 모델로 변환한다. UI 결합을 피하고 향후 3-way 결과 확장 여지를 남기되 미사용 3-way 계층은 만들지 않았다.
- 비교 정규화와 원문/원본 줄 매핑을 분리한다. 무시 옵션을 사용해도 병합은 실제 원문으로 수행해야 하기 때문이다.
- 모든 블록·전체 병합은 대상 Scintilla의 단일 ReplaceAll Undo 작업이며, 적용 뒤 새 Diff를 계산한다. 계산 전 좌표를 연속 사용하지 않는다.
- 현재 문서 endpoint는 시작 revision을 저장한다. 원본 반영 콜백이 revision을 다시 검사해 오래된 Diff가 원본 변경을 덮지 못하게 한다.
- ScintillaNET 공개 위치는 문자 기준이므로 Core의 UTF-16 inline span을 `GetLineTextRange`에서 한글·이모지 경계를 검증해 변환한다.
- Diff 창 종료 승인은 메인 문서 종료 확인과 함께 두 단계로 조정한다. 뒤 단계가 취소되면 앞서 준비한 Diff 종료 승인을 초기화해 다음 종료 때 변경을 다시 보호한다.
- 설정에는 옵션과 배치만 저장하며 비교 텍스트와 이력은 저장하지 않는다.

## 사용자가 명시한 요구사항 및 변경사항

- Diff / Merge는 기존 문서 탭과 분리된 모델리스 창이어야 한다.
- 첫 릴리스 범위는 기본 줄·단어 비교, 탐색·통계, 블록/전체 양방향 병합, 선택 영역 비교, 좌우 교환, 차이 복사다.
- 양쪽 편집기는 기본 편집 가능하고 클립보드·선택 영역은 기본 읽기 전용이다.
- Ignore Whitespace/Case/Empty Lines는 기본 OFF, Scroll Sync는 기본 ON이다.
- 공백 무시는 앞뒤 공백과 연속 공백·탭 차이를 무시하되 단어 경계는 유지한다.
- 원본 탭 반영은 명시적 동작이고 디스크 저장과 분리한다. 병합만으로 파일을 저장하지 않는다.
- DiffPlex 1.9.0을 사용한다.
- Heatmap, 동일 영역 접기, Smart Merge, Merge Basket, Bookmark/Memo, custom ignore regex, snapshot/timeline, 실제 3-way/AI 설명은 후속 범위다.
- 기존 대용량 로딩, 검색·변환·드래그 앤 드롭·단축키·종료 성능을 회귀시키지 않는다.

## 수정한 주요 파일과 역할

| 파일 | 역할 |
| --- | --- |
| `src/MyTextEditor.Core/Models/DiffModels.cs` | UI 독립 Diff 옵션·블록·줄 쌍·inline span·anchor·통계 모델 |
| `src/MyTextEditor.Core/TextDiffEngine.cs` | DiffPlex 줄/inline 비교, 정규화와 원본 줄 매핑 |
| `src/MyTextEditor.Core/TextMergeService.cs` | 블록·전체 양방향 병합과 줄바꿈/끝 개행 보존 |
| `src/MyTextEditor/Diff/DiffContracts.cs` | endpoint, 창 옵션, appearance, 메인 창 콜백 계약 |
| `src/MyTextEditor/Diff/DiffSourceDialog.xaml(.cs)` | 비교 대상 조합 선택과 파일 로드 |
| `src/MyTextEditor/Diff/DiffWindow.xaml(.cs)` | 모델리스 비교·편집·병합·저장·stale/종료 UI |
| `src/MyTextEditor/Controls/ScintillaEditorHost.cs` | Diff 표시, 범위 편집, viewport, Alt 단축키와 Handled 계약 |
| `src/MyTextEditor/MainWindow.Diff.cs` | Diff 창 실행, 원본 콜백, 창 추적, 설정·테마·종료 연결 |
| `src/MyTextEditor/MainWindow.xaml(.cs)` | 비교 메뉴/도구 버튼과 메인 수명 주기 통합 |
| `src/MyTextEditor/Models/UserSettings.cs`, `Services/SettingsService.cs` | Diff 옵션·창 배치 호환 저장 |
| `tests/MyTextEditor.Core.Tests/Program.cs` | Diff 옵션·줄 매핑·끝 개행·병합·5만 줄 테스트 |
| `tests/MyTextEditor.Performance/Program.cs` | 단축키 Handled, Unicode inline 위치, 30MB 회귀 검증 |
| `README.md`, `THIRD_PARTY_NOTICES.md` | 사용자 안내와 DiffPlex 라이선스 |

## 검증 결과

- Core 테스트: 30/30 통과
- 50,000줄·100개 변경 Diff: 약 51ms, 3초 기준 통과
- Release 솔루션 빌드: 경고 0, 오류 0
- Scintilla 단축키 전달과 편집 단축키 비가로채기: 통과
- UTF-8/NUL round-trip, 한글·이모지 inline 범위, 단일 Undo/Redo: 통과
- 30,000,044바이트 로그 3회 로딩 중앙값: 0.241초, 2초 기준 통과
- 30MB Scintilla 해제: 0.022~0.057초, 1.5초 기준 통과
- 자체 포함 EXE FileVersion: `1.5.0.0`; ProductVersion은 구현 커밋 `1c9f3dc`를 가리킴
- 배포 EXE 실제 시작·clean close: 58ms
- EXE 크기: 79,957,647바이트; SHA-256: `85491EDC642FBCEA046A96E1F6F8EA35775171DC281300C6AE98254532C080DB`
- ZIP 크기: 74,436,900바이트; SHA-256: `22497675D91F33688244B382DFDE53DA3DAB91FC5B672DB3F752F107125630AB`
- ZIP 내부 EXE와 독립 EXE SHA-256 일치
- GitHub 공개 Release와 두 자산 조회 확인: `https://github.com/pubill13/MyTextEditor/releases/tag/v1.5`

## 알려진 문제/미해결 이슈

- 100%·125%·150% DPI와 라이트·다크 테마의 실제 모니터별 육안 검증은 자동 환경에서 수행하지 못했다. XAML 빌드와 실행 검증으로 대체한다.
- 수정 Diff 두 쪽, 여러 일반 문서, 파일 외부 변경 경고의 모든 MessageBox 조합을 자동 클릭하는 UI 테스트는 없다. 핵심 상태 전이는 코드 검토와 Core/host 테스트로 검증했다.
- 줄·inline 강조는 구현됐지만 삭제만 있는 반대편의 가상 빈 행 정렬은 만들지 않는다. 스크롤은 동일 줄 anchor 보간으로 맞춘다.
- Diff 파일 로드는 현재 전체 문자열을 만든다. 5만 줄 비교 성능 목표에는 충분하지만 수백 MB 파일 동시 비교는 별도 최적화 대상이다.
- DiffPlex의 실제 3-way API는 이번 버전에서 노출하지 않는다.

## 다음에 해야 할 작업과 우선순위

1. 실제 사무실 데이터와 100%·125%·150% DPI에서 Diff 강조, gutter 위치, 다크 테마를 육안 확인한다.
2. 큰 추가·삭제 블록이 있는 파일로 anchor 보간 스크롤 감각과 gutter 버튼 위치를 조정한다.
3. 후속 요구가 생기면 Heatmap, 동일 영역 접기, Smart Merge 등 확정된 v1.5 제외 범위의 우선순위를 다시 정한다.

## 다음 Agent가 반드시 알아야 할 주의사항

- 루트 `AGENTS.md`를 먼저 읽고 사용자 변경을 reset·checkout·revert하지 않는다.
- Core에 WPF/Scintilla 타입을 넣거나 DiffPlex 모델을 UI에 직접 노출하지 않는다.
- `ApplyBlock` 뒤 기존 `DiffBlock` 좌표를 재사용하지 않는다. 반드시 전체 Diff를 다시 계산한다.
- 원본 탭 반영 시 endpoint revision을 재검사하고 `ReplaceAll` 한 번으로 적용하며 자동 저장하지 않는다.
- 클립보드·선택 endpoint는 파일 경로를 만들지 않고 기본 읽기 전용을 유지한다.
- Scintilla inline 강조에 UTF-8 바이트 길이를 직접 넣지 않는다. `GetLineTextRange`로 줄 기준 UTF-16 범위를 변환한다.
- Alt 방향키는 subscriber가 `Handled=true`로 응답한 Diff 창에서만 가로챈다. 일반 편집기 동작을 막지 않는다.
- 빠른 입력 직후 이전 `_result`를 병합 가능 상태로 남기지 않는다.
- 앱 종료 확인이 취소되면 `CancelPreparedClose()`로 이전 Diff 종료 승인을 초기화한다.
- `bin/`, `obj/`, `artifacts/`, 사용자 `settings.json`은 커밋하지 않는다. 배포 산출물은 GitHub Release에만 첨부한다.
- 변경 후 Release 빌드, Core 테스트, 성능 실행기, 실제 EXE 시작을 다시 검증한다.
