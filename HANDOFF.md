# MyTextEditor 작업 인수인계

최종 갱신: 2026-09-08 (Asia/Seoul)

## v1.2 인수인계 상태

현재 목표는 30MB·약 3천만 자 로그를 빠르게 여는 편집기, 파일 드래그 앤 드롭, 실행별 검색 결과 탭을 제공하는 `1.2.0` 배포다. WPF `TextBox`를 `Scintilla5.NET 7.0.0` 기반 `ScintillaEditorHost`로 교체했고, UTF-8 버퍼를 네이티브 편집기에 직접 한 번 전달한다. UTF-16·CP949만 UTF-8 편집 버퍼로 변환하며 저장 시 원래 인코딩과 BOM·줄바꿈을 보존한다.

### v1.2에서 완료한 작업

- `ScintillaEditorHost`에 로드, 텍스트 조회, 전체 교체, 줄 이동, Undo/Redo, save point, caret·revision·dirty 이벤트, 네이티브 파일 드롭을 캡슐화했다.
- `DocumentViewModel`의 양방향 전체 문자열 바인딩을 제거했다. Scintilla가 편집 원본이며 `ContentRevision`으로 결과 유효성을 판단한다.
- `DocumentLoadBuffer`와 `LoadBufferAsync`를 추가했다. 엄격한 UTF-8 검증, BOM 제거, UTF-16 LE/BE·CP949 변환과 NUL 보존을 테스트했다.
- 범위 기반 `TextSearchEngine.SearchRanges`를 추가해 모든 검색·문맥 줄 문자열 생성을 피했다. 기존 `Search` API는 유지한다.
- 창과 Scintilla 영역에서 단일·다중 파일 드롭을 받는다. 순서 유지, 열린 파일 중복 방지, 폴더 제외, 실패 일괄 안내를 공통 열기 경로에서 처리한다.
- 검색할 때마다 `SearchResultSession` 탭을 만든다. 같은 문서와 revision은 참조 계산된 `SearchSnapshot`을 공유하고 마지막 탭이 닫히면 해제한다.
- 결과 탭은 선택/전체 복사, 줄 번호 포함, 선택/전체 추출, 선택/전체 삭제 미리보기, 개별/다른/모두 닫기를 제공한다. 선택 복사는 문맥 행도 포함하고 전체 복사는 일치 행만 포함한다.
- 원문 수정 또는 닫힘 뒤에도 스냅샷 결과와 복사를 유지하고 원문 이동·추출·삭제만 비활성화한다. 변환은 별도 고정 `변경 미리보기` 화면에서 기존 내용을 교체한다.
- `THIRD_PARTY_NOTICES.md`에 Scintilla5.NET의 공식 출처와 MIT 라이선스를 기록했다.
- 단일 EXE는 Scintilla·Lexilla DLL을 리소스로 포함하고 최초 실행 시 `%LOCALAPPDATA%\MyTextEditor\native\7.0.0\win-x64`에 추출한다. `ScintillaNativeLibrary.SatelliteDirectory`를 이 위치로 지정하므로 별도 runtime 폴더 없이 실행된다.
- 3천만 자 UTF-8 성능 실행기를 추가했다. 이 PC의 3회 측정은 0.243s, 0.240s, 0.247s, 중앙값 0.243s로 2초 기준을 통과했다.
- Core 회귀 테스트는 19/19, Release 빌드는 경고 0·오류 0 상태다.

### 현재 수정 중인 작업과 다음 우선순위

구현은 통합됐고 최종 독립 리뷰, 실제 UI 흐름 검증, 자체 포함 publish, GitHub 배포가 남았다.

1. Reviewer가 결과 탭의 선택/전체 복사, stale 원문 차단, snapshot 참조 해제, 드롭 오류 집계를 독립 검토한다.
2. Release 빌드와 Core 19개 테스트, 성능 실행기를 다시 실행한다.
3. self-contained `win-x64` 단일 EXE를 publish하고 시작·종료 및 Scintilla native DLL 로딩을 확인한다.
4. EXE와 ZIP 해시를 확인한 뒤 main 커밋·push, `v1.2` 태그 및 GitHub Release를 만들고 두 자산을 첨부한다.

### v1.2 주요 파일

| 파일 | 역할 |
| --- | --- |
| `src/MyTextEditor/Controls/ScintillaEditorHost.cs` | WinFormsHost와 Scintilla 네이티브 편집기 경계 |
| `src/MyTextEditor.Core/DocumentFileService.cs` | 비동기 원본 바이트 로드, 인코딩 판별과 UTF-8 편집 버퍼 생성 |
| `src/MyTextEditor.Core/TextSearchEngine.cs` | span 기반 줄 평가와 범위 결과 |
| `src/MyTextEditor/Models/UiModels.cs` | 검색 스냅샷·세션·표시 행 모델 |
| `src/MyTextEditor/MainWindow.xaml` | 결과 탭·고정 미리보기·드롭 대상 UI |
| `src/MyTextEditor/MainWindow.xaml.cs` | 세션 수명, 결과 작업, 공통 다중 파일 열기 |
| `tests/MyTextEditor.Performance` | 3천만 자 파일 로드+첫 렌더 3회 중앙값 실행기 |
| `src/MyTextEditor/THIRD_PARTY_NOTICES.md` | Scintilla5.NET 출처와 MIT 고지 |

### v1.2 주의사항과 알려진 한계

- 검색 결과 탭은 실행 중 메모리에만 존재하며 앱 재시작 시 복원하지 않는다. 이는 확정 요구사항이다.
- 검색 스냅샷은 탭이 존재하는 동안 의도적으로 원문 문자열을 보유한다. 같은 문서·revision에서는 공유되지만 서로 다른 revision 검색은 별도 스냅샷이므로 많은 대형 로그 이력은 메모리를 사용한다.
- 검색 탭의 stale 여부는 문서 객체가 `Documents`에 남아 있고 revision이 같은지로 판단한다. 원문이 stale일 때 복사 기능까지 막는 회귀를 만들지 않는다.
- 변환 미리보기의 stale 방어는 검색 세션과 별도인 `_resultDocument`·`_resultSourceRevision` 경로를 사용한다.
- Scintilla는 WinForms 네이티브 컨트롤이므로 WPF 창의 drop 이벤트만으로는 편집 영역 드롭을 받지 못한다. host의 `FilesDropped` 연결을 유지한다.
- Scintilla5.NET은 기본적으로 EXE 옆 `runtimes\win-x64\native`를 요구한다. `EnsureNativeLibraries`의 내장 리소스 추출과 `SatelliteDirectory` 설정을 제거하면 단일 EXE가 시작 직후 종료된다.
- 로딩 성능 수치는 현재 로컬 SSD와 이 PC 기준이다. 성능 실행기는 실제 Scintilla 첫 Render까지 포함하지만 OS 파일 캐시 영향을 받는다.
- 아래 문서의 v1.1 설명 중 `TextBox` 구조와 15개 테스트 기록은 역사 정보다. 현재 구현 기준은 이 v1.2 섹션을 우선한다.

## 프로젝트의 현재 목표

복잡한 정규식이나 검색식을 외우지 않아도 줄 단위 검색·추출·삭제·자르기·정리 작업을 쉽게 수행할 수 있는 Windows 데스크톱 텍스트 편집기를 만든다. 대상 환경은 Windows 10/11 x64이며, C#·WPF·.NET 9로 구현한다. UI는 개발자 도구처럼 정돈된 밀도와 라이트·다크 테마를 제공하고, .NET이 설치되지 않은 PC에서도 단일 실행 파일로 사용할 수 있어야 한다.

개발 판단의 우선순위는 `Correctness → Simplicity → Readability → Maintainability → Testability`이다. 외부 서비스, 무거운 UI 프레임워크, 불필요한 계층을 추가하지 않는다.

## 현재 상태

v1 기능과 첫 번째 UX 확장 작업이 구현·검증된 상태다. 현재 구현 또는 수정 중인 미완료 코드는 없다. Release 빌드와 Core 테스트 15개가 모두 통과했고, 자체 실행 `win-x64` 단일 EXE와 ZIP도 생성·검증했다. `artifacts/`는 생성 산출물이므로 Git에서 제외하며, 사용자가 내려받을 배포본은 GitHub `v1.1` Release에 `MyTextEditor.exe`와 `MyTextEditor-win-x64.zip`으로 첨부했다.

### 완료된 작업

- 새 문서, 파일 열기, 저장, 다른 이름으로 저장, 탭 닫기
- 수정 상태가 표시되는 다중 문서 탭과 탭별 Undo/Redo
- 수정 문서 종료 시 저장·저장 안 함·취소 처리
- 수정 문서가 없을 때 재진입 없이 즉시 종료하는 경로
- UTF-8, UTF-16 BOM, Windows 한국어(CP949) 인코딩 감지와 기존 인코딩 보존
- CRLF, LF, CR 줄바꿈 감지와 저장 시 보존
- 기존 인코딩으로 표현할 수 없는 문자 저장 차단 및 UTF-8 저장 안내
- 임시 파일 작성 후 교체하는 안전 저장
- 간편 조건 검색
  - 모두 포함할 단어
  - 하나라도 포함할 단어
  - 제외할 단어
  - 쉼표 입력과 삭제 가능한 조건 태그
- 고급 조건 검색
  - 중첩 AND·OR 그룹과 포함·제외 조건
  - 전체 조건과 괄호 그룹을 구분하는 카드형 UI
  - 카드 안에서 조건·그룹 추가와 삭제
  - 최소 36px 조작 영역, hover, 빈 영역 클릭 시 입력 포커스
  - 새 조건 추가 후 자동 포커스
  - 괄호와 AND·OR 관계를 보존하는 자연어 요약
- 간편 조건을 고급 조건 트리로 변환
- 고급 조건에서 간편 모드로 돌아갈 때 조건 초기화 확인
- 부분 일치와 대소문자 무시를 기본으로 한 검색
- 완전한 단어, 대소문자 구분, 주변 문맥 옵션
- 검색 결과 줄 번호, 원문 이동, 결과 복사, 새 탭 추출, 일치 줄 삭제
- 결과·미리보기 대상 문서와 원문 변경 여부를 검사하는 stale-result 방어
- 결과 패널 기본 접힘과 검색·미리보기 시 자동 열림
- 미리보기 `전체·변경·건너뜀` 필터 및 변경 0건일 때 적용 비활성
- 한 번의 대량 변경을 한 번의 Undo로 복원
- 유용한 기능
  - 기준 앞쪽 제거
  - 기준 뒤쪽 제거
  - 두 기준 사이 내용 삭제
  - 두 기준 사이 내용만 남기기
  - 각 줄 왼쪽·오른쪽 N글자 제거
  - 각 줄 접두사·접미사 추가
  - 구분자를 줄바꿈으로 변경
  - 여러 줄을 구분자로 합치기
  - 시작 번호와 구분자로 줄 번호 추가
  - 편집기 형식의 줄 번호 제거
- Unicode 문자소 단위 글자 제거: 이모지와 조합 문자를 중간에서 자르지 않음
- 빠른 정리: 중복 줄 제거, 빈 줄 제거, 연속 빈 줄 합치기, 앞뒤 공백 제거, 일괄 치환
- 설치된 Windows 글꼴 검색과 크기 선택
- 설치된 경우 맑은 고딕, D2Coding, 나눔고딕, Noto Sans KR, Pretendard, Cascadia Mono, Consolas, JetBrains Mono 및 Windows 기본 한글 글꼴을 우선 표시
- 저장 글꼴이 사라진 경우 맑은 고딕 또는 Consolas로 대체
- 좁은 창에서 글꼴·크기 설정을 오버플로 팝업으로 이동
- 라이트·다크 테마와 동적 리소스 기반 상태 스타일
- 창 크기, 위치, 패널 상태, 테마, 글꼴, 최근 파일 설정 보존
- 설정이 실제로 변경된 경우에만 임시 파일을 거쳐 원자적으로 저장
- 앱 아이콘과 빈 문서 화면
- .NET 설치가 필요 없는 `win-x64` 단일 EXE와 ZIP 생성

## 주요 설계 결정과 이유

### 프로젝트 분리

- `MyTextEditor.Core`: WPF에 의존하지 않는 검색·변환·파일 처리 로직이다. UI 없이 빠르게 검증하고 재사용할 수 있게 분리했다.
- `MyTextEditor`: WPF 화면, 문서 탭, 사용자 상호작용과 설정을 담당한다.
- `MyTextEditor.Core.Tests`: 외부 테스트 프레임워크 없이 실행되는 assertion 프로그램이다. 네트워크 패키지 의존 없이 전체 Core 회귀 검증을 실행하려는 선택이다.

### 문서와 Undo

각 문서는 자신의 WPF `TextBox` 인스턴스를 유지한다. 탭을 바꿔도 문서별 Undo 스택이 섞이거나 사라지지 않으며, 변환 적용 시 `SelectAll()`과 `SelectedText` 변경을 사용해 전체 변환 하나가 Undo 한 번으로 복구된다.

### 검색 조건

기본 사용자는 세 개의 간편 입력으로 대부분의 검색을 해결한다. 복잡한 괄호가 필요할 때만 고급 그룹을 사용한다. 간편 조건은 루트 AND 아래에 `모두 포함` 조건, `하나라도 포함`용 OR 하위 그룹, 제외 조건으로 변환한다. 간편→고급 전환 시 루트 연산자를 반드시 AND로 초기화해야 한다.

### 텍스트 변환

모든 변환은 `TextTransformResult`와 줄별 `TextChangePreview`를 반환한다. UI는 결과를 바로 적용하지 않고 미리보기와 통계를 먼저 보여준다. 글자 수 제거는 UTF-16 코드 단위가 아닌 `StringInfo.ParseCombiningCharacters` 기반 Unicode 문자소 단위로 계산한다.

### 파일과 설정 저장

원본 파일의 인코딩과 줄바꿈을 보존한다. 파일과 설정은 임시 파일을 먼저 완성한 뒤 대상 경로로 교체해 중간 실패로 기존 파일이 손상될 가능성을 줄인다. 설정은 시작 시 직렬화한 snapshot과 현재 값을 비교해 실제 변경이 있을 때만 기록한다.

### 종료 처리

수정된 문서가 없으면 `Closing` 이벤트를 취소하지 않고 설정만 저장한 뒤 첫 요청에서 종료한다. 수정 문서가 있을 때만 비동기 확인 경로를 시작하고, 전체 확인이 성공한 경우에만 `_allowClose`를 설정해 마지막 `Close()`를 호출한다. 취소하면 탭과 저장하지 않은 내용을 유지한다.

## 사용자가 명시한 요구사항과 변경사항

- `AAA`와 `BBB`가 모두 들어가고 `CC`가 들어간 줄은 제외하는 식의 검색을 쉽게 구성해야 한다.
- 특정 텍스트 기준 앞쪽 제거, 뒤쪽 제거, 두 기준 사이 삭제가 필요하다.
- 상용 도구처럼 글꼴, 아이콘, 테마와 전체 UI/UX 완성도를 신경 써야 한다.
- 선택한 조건을 하단 `+/-`로 조작하던 방식은 클릭하기 어려우므로 제거한다.
- 조건 행 안에서 직접 조건·그룹을 추가하고 삭제해야 한다.
- 전체 조건과 하위 그룹의 관계를 자연어와 괄호 개념으로 설명해야 한다.
- 편집기 줄 간격과 상단 글꼴 영역을 더 조밀하게 만들고 맑은 고딕 등 흔한 글꼴을 추가해야 한다.
- `자르기` 카드 이름을 `유용한 기능`으로 변경하고 실용적인 변환을 확장해야 한다.
- 기존 `빠른 정리` 이름과 기능은 유지한다.
- 결과 패널은 기본적으로 접고, 미리보기 필터와 변경 0건 적용 방지를 제공해야 한다.
- 수정되지 않은 빈 문서 하나도 종료할 때 버벅거리지 않아야 한다.
- 조건 전환 과정에서 조건을 조용히 잃거나 검색 의미가 바뀌면 안 된다.
- 모든 대량 작업은 미리보기 후 한 번의 Undo 단위로 적용해야 한다.
- v1 이후 범위: 초대용량 로그 스트리밍, 정규식 직접 입력, 파일 비교, 코드 구문 강조, 플러그인, 클라우드 동기화.

## 주요 파일과 역할

| 파일 | 역할 |
| --- | --- |
| `AGENTS.md` | 이 저장소에서 계속 지켜야 할 개발 원칙과 완료 기준 |
| `README.md` | 사용자용 기능, 실행, 검색·변환 예시, 단축키, 배포 방법 |
| `MyTextEditor.sln` | 전체 솔루션 |
| `Directory.Build.props` | Nullable, warnings-as-errors, Release 설정 |
| `src/MyTextEditor.Core/TextSearchEngine.cs` | 조건 트리 평가와 줄 검색 |
| `src/MyTextEditor.Core/TextTransformService.cs` | 자르기, 변환, 정리, 미리보기 결과 생성 |
| `src/MyTextEditor.Core/DocumentFileService.cs` | 인코딩·줄바꿈 감지와 안전 저장 |
| `src/MyTextEditor.Core/Models/SearchModels.cs` | 검색 조건 트리와 결과 모델 |
| `src/MyTextEditor.Core/Models/TransformModels.cs` | 변환 옵션, 상태, 미리보기 모델 |
| `src/MyTextEditor/MainWindow.xaml` | 편집기, 도구 패널, 결과 패널의 WPF 레이아웃 |
| `src/MyTextEditor/MainWindow.xaml.cs` | 문서 탭, 검색 조건 UI, 변환 연결, 종료와 설정 흐름 |
| `src/MyTextEditor/Models/DocumentViewModel.cs` | 문서 내용, 경로, 변경 상태, 인코딩, 줄바꿈, Editor 인스턴스 |
| `src/MyTextEditor/Services/SettingsService.cs` | 사용자 설정 로드와 원자 저장 |
| `src/MyTextEditor/Themes/*.xaml` | 공통 컨트롤 스타일과 라이트·다크 색상 |
| `tests/MyTextEditor.Core.Tests/Program.cs` | 15개 Core 회귀 테스트 |
| `tools/Generate-AppIcon.ps1` | 앱 아이콘 생성 도구 |

## 검증 기록

2026-09-08 기준:

- `dotnet build MyTextEditor.sln -c Release`: 성공, 경고 0, 오류 0
- `dotnet run --project tests/MyTextEditor.Core.Tests/MyTextEditor.Core.Tests.csproj -c Release`: 15/15 통과
- Release GUI 시작 스모크: 통과
- 수정 없는 창의 실제 종료: 약 47ms
- 최종 배포 ZIP 해제 후 EXE SHA-256 일치 확인
- 배포 EXE SHA-256: `633CB8395BB33472B43E5BF1FA9DD35738869611B2478B940258132B0B78D20E`
- 독립 Reviewer 최종 판정: 남은 BLOCKER/P1/P2 없음

검증 명령:

```powershell
dotnet build MyTextEditor.sln -c Release
dotnet run --project tests/MyTextEditor.Core.Tests/MyTextEditor.Core.Tests.csproj -c Release
dotnet publish src/MyTextEditor/MyTextEditor.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

## 알려진 문제와 미해결 이슈

- 실제 100%, 125%, 150% Windows DPI 환경을 자동 UI 상호작용으로 모두 재현하지는 못했다. 1380×860 라이트 테마 렌더링, XAML 구조, 가로 스크롤과 오버플로 대응을 검토했다.
- 수정 문서가 여러 개인 경우의 모든 저장 대화상자 조합은 자동 UI 테스트가 아니라 코드 경로와 수동 스모크 중심으로 확인했다.
- 간편 조건 입력은 쉼표를 구분자로 사용하므로 검색어 자체에 쉼표를 포함하는 입력 형식은 아직 제공하지 않는다.
- 대용량 파일은 전체 내용을 메모리에 올리고 WPF `TextBox`로 표시한다. 초대용량 로그 스트리밍은 의도적으로 v1 이후 범위다.
- `artifacts/`의 EXE와 ZIP은 Git 히스토리에 포함되지 않고 GitHub Releases에 첨부한다. 현재 배포 위치는 `https://github.com/pubill13/MyTextEditor/releases/tag/v1.1`이다. 새 소스 변경 후에는 다시 publish·ZIP 검증을 수행하고 새 버전 Release에 자산을 올려야 한다.

## 다음 작업과 우선순위

1. 100%, 125%, 150% DPI와 최소 창 너비에서 라이트·다크 테마를 실제 화면으로 확인한다.
2. 간편 조건 태그 입력을 직접 칩 입력 방식으로 더 다듬을지 사용자 피드백을 받는다. 현재는 쉼표 입력값을 태그로 렌더링하고 개별 삭제를 지원한다.
3. 여러 수정 탭에서 `저장`, `저장 안 함`, `취소` 조합을 실제 UI 자동화 또는 수동 체크리스트로 회귀 검증한다.
4. 사용자 피드백에 따라 결과 패널 열 너비, 도구 패널 밀도, 키보드 탐색을 다듬는다.
5. 기능 확장은 사용자 우선순위를 확인한 뒤 v1 이후 후보에서 선택한다.

## 다음 Agent가 반드시 알아야 할 주의사항

- 작업 전에 루트 `AGENTS.md`와 이 문서를 먼저 읽는다.
- 사용자가 만든 변경을 임의로 reset, checkout, revert하지 않는다.
- Core는 WPF에 의존하게 만들지 않는다. UI 기능도 가능한 한 기존 `TextSearchEngine`과 `TextTransformService`를 사용한다.
- 간편 조건을 고급 트리로 옮길 때 `_rootCondition.MatchAll = true`를 유지한다. 이전 고급 OR 상태를 상속하면 검색 의미가 바뀌는 회귀가 생긴다.
- 고급 조건 요약을 평탄화하지 않는다. 재귀 요약에서 AND·OR와 괄호를 보존해야 한다.
- 고급 카드의 테마 Brush는 정적 객체 대입 대신 `SetResourceReference` 또는 DynamicResource를 사용한다. 그래야 실행 중 테마 전환이 기존 카드에도 반영된다.
- 줄 번호 추가·제거에서 빈 구분자를 Core에 전달하면 `ArgumentException`이 발생한다. UI에서 먼저 검증한다.
- 검색 결과나 변환 미리보기 적용 전에 대상 문서와 원문 snapshot이 같은지 검사하는 stale-result 방어를 제거하지 않는다.
- 문서별 `TextBox`와 전체 선택 후 `SelectedText` 교체 방식은 탭별 Undo와 단일 Undo 요구사항 때문에 선택한 구조다.
- clean close 경로에서 `e.Cancel = true` 또는 재귀 `Close()`를 다시 도입하지 않는다.
- 새 변환을 추가하면 `TextTransformResult`와 미리보기 모델을 재사용하고 한글, 이모지, 빈 줄, 기준 누락, 구분자 반복 경계를 테스트한다.
- 설정과 문서 저장의 임시 파일 교체 방식을 유지한다.
- 코드 변경 후 최소한 Release 빌드와 Core 전체 테스트를 실행한다. 배포를 갱신했다면 실제 EXE 시작과 ZIP 내부 해시도 확인한다.
- `bin/`, `obj/`, `artifacts/`, 사용자 설정 파일을 커밋하지 않는다.
