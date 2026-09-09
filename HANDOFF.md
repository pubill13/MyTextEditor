# MyTextEditor 작업 인수인계

최종 갱신: 2026-09-09 (Asia/Seoul)

## 프로젝트의 현재 목표

MyTextEditor는 복잡한 정규식 없이 줄 단위 검색·추출·삭제·가공을 수행하는 Windows 10/11 x64용 텍스트 편집기다. C#·WPF·.NET 9와 Scintilla5.NET을 사용하며, 30MB·약 3천만 자 로그를 2초 이내에 편집 가능한 상태로 여는 성능과 .NET 설치가 필요 없는 단일 EXE 배포를 유지한다.

현재 개발 목표는 `1.4.0`이다. 검색 화면을 세 입력으로 단순화하고, 상용 편집기 단축키와 모든 정리 기능의 바로 적용, 보수적인 로그 정리, 빠른 종료를 제공한다.

## v1.4에서 완료한 작업

- 검색 UI에서 간편/고급 전환과 고급 그룹 카드를 제거했다. `모두 포함`, `하나라도 포함`, `제외` 입력만 표시한다.
- Core 조건 트리는 내부 구현으로 유지한다. 세 입력을 루트 AND, 포함 조건, OR 하위 그룹, 제외 조건으로 변환한다.
- 구버전 고급 검색은 세 입력으로 정확히 표현할 수 있을 때 자동 변환한다. 중첩 그룹처럼 변환할 수 없는 현재 조건과 최근 기록은 제거하고 한 번 알린다. 마이그레이션 결과는 즉시 원자 저장해 다음 실행에 반복되지 않는다.
- Scintilla 포커스에서도 파일·탭·찾기·바꾸기 단축키가 공통 명령 라우터로 전달된다. `Ctrl+Z/Y/X/C/V/A`는 포커스가 있는 입력 컨트롤이 직접 처리한다.
- `F3`과 `Shift+F3`은 문맥 행을 건너뛰고 실제 일치 행만 순환하며, 원문 snapshot이 유효하면 해당 문서와 줄로 이동한다.
- 자르기, 좌우 글자 제거, 접두·접미, 분할·합치기, 줄 번호, 포함 행 삭제, 치환, 빠른 정리에 `미리보기`와 `바로 적용`을 제공한다. 직접 적용은 확인창 없이 Undo 한 번으로 복구된다.
- 잘못된 입력은 상태바에 이유를 표시하고 해당 입력칸으로 포커스를 이동한다. 변경 0건은 적용하지 않고, 직접 적용 시 이전 변환 미리보기를 폐기한다.
- `로그 정리`를 빠른 정리와 즐겨찾기에 추가했다. 완성된 ANSI CSI/OSC, NUL과 탭·개행을 제외한 C0/DEL, 줄 끝 공백·탭을 제거하고 연속 빈 행을 하나로 줄인다.
- 로그 정리는 정규식이나 새 의존성 없이 한 번 순회한다. 들여쓰기, 타임스탬프, 레벨, 행 순서, 중복 내용, JSON, 구분자, 문자열 형태의 `\\n`, 줄바꿈과 끝 개행을 보존한다.
- 종료 시 설정 debounce를 멈추고 창을 먼저 숨긴 뒤 변경 설정만 저장하고 Scintilla와 검색 snapshot을 해제한다. 설정 저장 오류가 나면 창을 다시 표시해 알린다.
- 문서별 닫기 재진입과 저장 중 선해제를 막았다. 모든 탭 닫기는 전체 확인 성공 뒤에만 제거하며, 저장·닫기 중 창 종료 재진입도 차단한다.
- 고급 검색 UI 전용 `ConditionEditorNode`를 제거했다. 호환 DTO와 Core 조건 트리는 구버전 설정 마이그레이션 때문에 유지한다.
- 앱 버전을 `1.4.0`으로 올리고 README의 기능·검색·로그 정리·단축키 설명을 갱신했다.

## 현재 구현 또는 수정 중인 작업

v1.4 소스 구현, 교차 리뷰, 로컬 검증, 배포가 모두 끝났다. 현재 구현 또는 수정 중인 미완료 작업은 없다. `main`의 구현 커밋은 `3ab9e3b`, 태그는 `v1.4`이며 GitHub Release는 `https://github.com/pubill13/MyTextEditor/releases/tag/v1.4`다. Release에 `MyTextEditor.exe`와 `MyTextEditor-win-x64.zip`이 첨부되어 있다.

## 주요 설계 결정과 그 이유

- WPF는 화면과 사용자 흐름만 담당하고 검색·변환·파일 처리는 `MyTextEditor.Core`에 둔다. UI 없이 기능을 검증하고 기존 경계를 유지하기 위해서다.
- Scintilla를 문서 원본으로 사용한다. WPF 문자열 양방향 바인딩을 피해야 큰 로그에서 전체 복사와 렌더 지연이 생기지 않는다.
- 고급 검색 DTO는 읽기 호환에만 사용한다. 새 설정은 항상 simple mode와 세 입력값을 저장한다.
- 로그 정리는 의미를 추측하지 않는다. 화면 표시 잡음과 명백한 제어문자·뒤 공백·과도한 빈 행만 바꿔 로그 의미 손상 가능성을 낮춘다.
- 바로 적용도 기존 `TextTransformResult`를 거친다. 미리보기와 직접 적용이 같은 Core 결과를 사용해야 동작 차이가 생기지 않는다.
- Scintilla의 앱 단축키만 `ProcessCmdKey`에서 가로채 Dispatcher로 창에 전달한다. 닫기 명령이 키 처리 스택 안에서 native control을 Dispose하지 않게 하기 위해서다.
- clean close에서는 `Closing`을 취소하고 다시 `Close()`하지 않는다. 창을 먼저 숨기고 설정 저장과 native 해제를 수행해 체감 지연과 재진입을 줄인다.

## 사용자가 명시한 요구사항 및 변경사항

- 검색은 `모두 포함·하나라도 포함·제외`만 남기고 고급 UI를 완전히 제거한다.
- 현재 조건, 최근 검색, 대소문자, 완전한 단어, 문맥 옵션은 계속 저장한다.
- 상용 편집기의 기본 파일·탭·찾기·바꾸기·탐색 단축키를 지원한다.
- 모든 텍스트 정리 기능은 미리보기와 확인 없는 바로 적용을 함께 제공하고 Undo 한 번으로 복구한다.
- 로그 정리는 보수적으로 ANSI·C0/DEL·뒤 공백·연속 빈 행만 정리한다. 열 정렬, 타임스탬프 변경, 중복 삭제, 여러 행 병합은 하지 않는다.
- 앱 종료를 빠르게 보이게 하고 실제 native 리소스 해제 시간도 줄인다.
- 외부 라이브러리를 새로 추가하지 않는다.
- 기존의 대용량 로그 성능, 파일 드롭, 검색 결과별 탭과 snapshot, 테마·글꼴·설정 자동 저장, 인코딩·줄바꿈 보존을 회귀시키지 않는다.

## 수정한 주요 파일과 역할

| 파일 | 역할 |
| --- | --- |
| `src/MyTextEditor/MainWindow.xaml` | 단순 검색 UI, 공통 메뉴, 미리보기·바로 적용 버튼, 로그 정리 행 |
| `src/MyTextEditor/MainWindow.xaml.cs` | 검색 캡처, 명령 라우터, 직접 적용, 로그 정리 연결, 닫기·종료 흐름 |
| `src/MyTextEditor/Controls/ScintillaEditorHost.cs` | native 포커스 단축키 전달과 idempotent 리소스 해제 |
| `src/MyTextEditor.Core/TextTransformService.cs` | `CleanupLog` 구현 |
| `src/MyTextEditor.Core/Models/TransformModels.cs` | 로그 정리 결과와 상세 통계 모델 |
| `src/MyTextEditor/Models/SettingsModels.cs` | simple 검색 생성, legacy 변환, 로그 정리 도구 ID |
| `src/MyTextEditor/Services/SettingsService.cs` | legacy 검색 변환·제거·1회 저장 신호 |
| `src/MyTextEditor/Models/UiModels.cs` | 고급 UI 전용 모델 제거 |
| `tests/MyTextEditor.Core.Tests/Program.cs` | 로그 정리 회귀 테스트 |
| `tests/MyTextEditor.Performance/Program.cs` | 단축키·설정 변환·native 해제·30MB 성능 검증 |
| `README.md` | v1.4 사용자 기능과 단축키 문서 |

## 검증 결과

- Release 솔루션 빌드: 경고 0, 오류 0
- Core 회귀 테스트: 24/24 통과
- Scintilla 단축키 전달, 편집 단축키 비가로채기, 반복 리소스 해제: 통과
- UTF-8/NUL round-trip과 전체 변환 단일 Undo/Redo: 통과
- legacy 검색 변환 가능·불가능 사례와 최근 검색 제한: 통과
- 30,000,044바이트 로그 3회 로딩 중앙값: 0.291초로 2초 기준 통과
- 30MB Scintilla 리소스 해제: 0.060~0.066초로 1.5초 기준 통과
- 자체 포함 단일 EXE FileVersion: `1.4.0.0`
- 배포 EXE 실제 시작·clean close: 65ms
- ZIP 내부 EXE와 독립 EXE SHA-256 일치
- EXE SHA-256: `3048229A13F2E7E7B728398412EB928DF112283AA284DD5D06C30C1BB5BE230D`
- ZIP SHA-256: `5E2E92486B36B72D3B9C48323354CD1336E0FC7EB2A13FD505A1A487CC704386`
- GitHub Release: `https://github.com/pubill13/MyTextEditor/releases/tag/v1.4`
- 공개 자산 크기: EXE 79,907,327바이트, ZIP 74,387,138바이트

## 알려진 문제와 미해결 이슈

- 100%·125%·150% DPI와 라이트·다크 테마의 실제 모니터별 육안 검증은 이번 자동 환경에서 수행하지 못했다. XAML 빌드와 GUI 시작은 검증했다.
- 수정 문서의 저장/저장 안 함/취소 조합은 코드 경로와 교차 리뷰로 확인했으며 완전한 UI 자동화는 없다.
- 로그 정리 미리보기는 기존 변환 구조처럼 전체 줄 preview 모델을 만든다. 30MB 로그 직접 적용은 선형 처리지만 미리보기는 결과 행 수만큼 추가 메모리를 사용한다.
- 검색 결과 snapshot은 결과 탭이 남아 있는 동안 원문을 보유한다. 같은 문서·revision은 공유하지만 서로 다른 revision은 별도 메모리를 쓴다.
- 검색 입력은 쉼표 구분이므로 검색어 자체에 쉼표를 넣는 escape 문법은 없다.
- `SavedSearchMode.Advanced`, `SavedConditionNode`, `LegacySearchMigration`은 UI 잔재가 아니라 v1.3 설정 호환 코드다.

## 다음에 해야 할 작업과 우선순위

1. 실제 사무실 로그로 로그 정리 결과와 30MB 미리보기 메모리 사용량을 확인한다.
2. 100%·125%·150% DPI와 라이트·다크 테마에서 새 빠른 정리 3열 UI의 잘림과 클릭 영역을 육안 점검한다.
3. 수정 탭 여러 개의 저장/저장 안 함/취소 및 저장 오류를 실제 UI로 회귀 확인한다.

## 다음 Agent가 작업을 이어갈 때 반드시 알아야 할 주의사항

- 루트 `AGENTS.md`를 먼저 읽고 사용자 변경을 reset·checkout·revert하지 않는다.
- Core에 WPF 의존성을 추가하지 않는다. 새 텍스트 변환은 기존 `TextTransformResult` 흐름을 재사용한다.
- 고급 검색 UI를 다시 노출하지 않는다. 호환 DTO는 v1.3 settings migration에 필요하므로 제거하지 않는다.
- 구버전 설정 변환 뒤 `NeedsSave` 경로를 유지해야 마이그레이션 안내가 매 실행 반복되지 않는다.
- `Ctrl+Z/Y/X/C/V/A`를 앱 shortcut router에서 가로채지 않는다.
- Scintilla에서 전달된 닫기 명령은 Dispatcher 밖에서 즉시 실행하지 않는다. 키 처리 중 native control을 Dispose할 수 있다.
- 문서를 닫을 때 제거 뒤 리소스를 해제하고, 비동기 저장 중 동일 문서 닫기 재진입을 허용하지 않는다.
- clean close에 `e.Cancel = true`와 재귀 `Close()`를 다시 넣지 않는다. 수정 문서는 모든 확인이 끝나기 전 창을 숨기지 않는다.
- 로그 정리에 정규식, C1 문자 일괄 제거, 의미 기반 재포맷, 중복 삭제를 임의로 추가하지 않는다.
- 검색 snapshot이 stale이어도 결과 복사는 계속 가능해야 하며 원문 변경 동작만 막는다.
- `bin/`, `obj/`, `artifacts/`, 사용자 `settings.json`은 커밋하지 않는다. 배포 산출물은 GitHub Release에만 첨부한다.
- 변경 후 Release 빌드, Core 테스트, 성능 실행기, 실제 EXE 시작을 다시 검증한다.
