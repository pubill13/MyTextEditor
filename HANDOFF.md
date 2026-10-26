# OmniEdit 새 세션 인수인계

2026-10-26. 신규 작업은 미완료·미배포. AGENTS.md와 이 문서를 먼저 읽고 필요한 코드만 확인한다. 과거 이력은 docs/HANDOFF-HISTORY-2026-10-26.md로 분리했으며 기본적으로 읽지 않는다. 계정 전환 시 반복 탐색·테스트로 사용량을 낭비하지 않도록 현재 남은 작업에 집중한다.

## 목표·Git
- .NET9 WPF/Scintilla 편집기의 인코딩·줄바꿈 표시·분할 자동 해제·외부 변경 반영·Merge 화면 밀도 개선을 마무리한다.
- main. 배포 v1.10(1.10.0)/9d8f27d는 EXE/ZIP 게시 완료. 신규 코드 포함 안 됨.
- 신규 로컬 커밋 5158d1b(기능), 9b57c54(인코딩 테스트). 마지막 origin/main 기준 아직 push 안 됨. 이번 문서 커밋은 뒤에 추가된다.
- .gitignore는 사용자 변경: 수정/stage/되돌리기 금지. csproj는 아직1.10.0, 신규 버전 미정. 기존 태그/릴리즈 덮어쓰기 금지.
- 이번 세션은 문서만 정리했고 개발·테스트·배포를 재실행하지 않았다.

## 확정 요구·작성된 코드
- UTF8 무BOM/BOM, UTF16 LE/BE BOM, Windows949 저장 형식 변경과 지정 인코딩 재열기 둘 다 제공. 재열기 전 저장/버리기/취소, 표현 불가 문자 차단. 인코딩 변경도 dirty.
- 일반 문서만 자동 줄바꿈, 기본OFF. 실제 개행/Undo 불변. 문서별 설정, 마지막 선택은 새 문서 기본값. Merge는 대응 줄/성능 때문에 제외.
- 이동/닫기 완료 후 한쪽이 비면 분할 해제, 오른쪽만 남으면 왼쪽 이동. 활성 문서/Undo/스크롤 유지.
- 외부 반영은 상단 부가 기능 토글. 메타데이터 일괄 확인,500ms 안정화, 비활성 문서는 선택 시 로드, 동시에 한 로드. 수정/저장/재열기 보호, revision/파일 시각 재검사, OFF/종료 취소, 단계별 시간.
- Merge +는 탭바, 크기/닫기/도움말은 메뉴. 한 줄 명령바, 옵션 메뉴/변경개수/자동수동 표시. 범례 팝업/통계 하단, gutter48px/벡터12px/클릭최소24px. F5/F7/F8/잠금/Undo 유지.
- 새 의존성 없음. 구현은 작성되었으나 아래 검증/수정이 남았다.

## 주요 파일
- Core/DocumentEncodings.cs: DocumentEncodingKind5종, GetEncoding/HasBom/GetDisplayName/FromEncoding. DocumentFileService.cs: 기존 자동감지 유지, LoadAsync/LoadBufferAsync(path,kind,token). Models/DocumentState.cs: ReadElapsed/DecodeElapsed.
- MainWindow.Encoding.cs: 메뉴/재열기/줄바꿈. Models/DocumentViewModel.cs: EncodingModified와 본문dirty 결합. UserSettings.cs: DefaultWordWrap.
- Controls/ScintillaEditorHost.Extras.cs: SetWordWrap/가로스크롤. MainWindow.DocumentPanes.cs: NormalizeDocumentPanes.
- MainWindow.FileSync.cs: 일괄감시/안정화/지연로드/시간. _encodingReloads 제외 확인. MainWindow.xaml(.cs): 메뉴/토글/저장 연결.
- Diff/DiffWorkspaceWindow.xaml, DiffTabView.xaml(.cs), DiffTabView.Comparison110.cs: compact UI. 옵션 CheckBox→MenuItem 변경, 테스트 캐스트 수정.
- Core.Tests/ExplicitEncodingTests.cs 신규. Performance/FileSyncVerification.cs, Merge110Verification.cs, V18Verification.cs 수정. 요청한 DocumentDisplayVerification 통합 테스트는 작성되지 않았다.

## 검증 사실·현재 장애
- 이전 실행 Core42/42, 앱Release0경고0오류, --merge-110/--merge-clarity/--result-font PASS. 이후 fallback/줄바꿈 코드가 다시 변경되어 최종HEAD 전체검증으로 취급하지 않는다.
- 이전 세션 Performance bin/obj 삭제 후 restore 실패. 현재 tests/MyTextEditor.Performance/obj/project.assets.json 없음(이번 세션 확인). 최종 솔루션 NETSDK1004 실패, 앱 단독 빌드는 성공했던 상태.
- restore 오류0개 실패 로그 artifacts/perfrestore.txt, 빌드 artifacts/builddiag.txt. 파일잠금 원인이라는 이전 설명은 확인되지 않은 추정이다.
- 저장/native DLL 추출 UnauthorizedAccessException 뒤 원인검증 없이 fallback 추가: 문서저장 Move→Replace; native추출 Move→Replace→Copy. 샌드박스/권한 원인을 먼저 조사하고 불필요/비원자적 workaround 재검토. 권한 문제면 require_escalated 재검증, 앱 코드로 더 우회 금지.
- 30MB0.221초/300MB Core비교/자체포함EXE 확인은 기존v1.10 기록. 신규외부반영30/100/300MB 실측, 실제DPI125/150%, 물리IME 미검증.

## 다음 작업 우선순위
1. restore/빌드 복구. 원인 없이 bin/obj 삭제 반복 금지. 명령별 exit code 확인, 빌드 실패 후 --no-build 테스트 금지. 샌드박스 의심 실패는 승인된 실행으로 재시도.
2. 줄바꿈 미완료 검토: custom 세로scrollbar와 SetFirstVisibleLine이 물리LineCount로 제한되어 긴 한 줄이 여러 표시 줄로 감길 때 틀릴 수 있다. 표시줄/문서줄 매핑, 커서/선택/스크롤 유지 구현·테스트. 이전 표시줄 계산은 검증 없이 제거됨.
3. 인코딩metadata dirty/Undo/저장/종료 확인, 재열기실패/취소/동시편집, 분할이동/닫기/취소 통합테스트. 인코딩 원복시 dirty정리와 encoding-only dirty의 MarkSaved이벤트 의존성 검토.
4. --file-sync 및 Merge/결과 회귀. OFF/닫기/재열기/편집 보호,30/100/300MB 반영 실측. 관련 신규 테스트 추가.
5. 최소/넓은창·5테마 Merge본문/버튼/메뉴 검증. README 옛 보기→외부파일변경자동반영 설명 및 도움말 신규 설명 미완료: 실제코드와 일치시킨다.
6. 리뷰/검증 후 완료 판단. 배포는 사용자 범위에 맞춰 새 버전으로 처리. 현재 완료/배포했다고 보고 금지. 진행 후 현재 상태만 짧게 갱신.

## 재개 명령
루트 PowerShell 한 줄씩 실행. GUI 테스트에서 기존 사용자 프로세스/미저장 창을 임의 종료하지 않는다.

    git status --short
    dotnet restore tests/MyTextEditor.Performance/MyTextEditor.Performance.csproj -p:NuGetAudit=false /nodeReuse:false /m:1
    Test-Path tests/MyTextEditor.Performance/obj/project.assets.json
    dotnet build MyTextEditor.sln -c Release --no-restore -p:NuGetAudit=false /nodeReuse:false /m:1
    dotnet run --project tests/MyTextEditor.Core.Tests -c Release --no-build --no-restore
    dotnet run --project tests/MyTextEditor.Performance -c Release --no-build -- --file-sync
    dotnet run --project tests/MyTextEditor.Performance -c Release --no-build -- --merge-110
    dotnet run --project tests/MyTextEditor.Performance -c Release --no-build -- --merge-clarity
    dotnet run --project tests/MyTextEditor.Performance -c Release --no-build -- --result-font

과거 대화/agent 메모리에 의존하지 않는다. 필요한 부분만 읽고 현재 변경에 집중한다.
