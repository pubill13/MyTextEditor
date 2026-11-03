# OmniEdit 인수인계

2026-10-27. AGENTS.md와 이 문서부터 읽고 현재 요청 관련 코드만 확인한다. 과거 기록은 docs/HANDOFF-HISTORY-2026-10-26.md에 있으며 기본적으로 읽지 않는다.

## 현재 작업
- v1.11.0은 GitHub v1.11 태그로 EXE/ZIP 공개 완료.
- 후속 v1.11.1: 줄바꿈·인코딩 메뉴 수정, Merge UI 단순화, 파일 변경 모니터링 명칭 통일. 소스 검증 및 EXE 시작 확인 완료. 872e655 / v1.11.1로 GitHub EXE·ZIP 게시 완료, 서버 SHA-256 일치 확인.
- .gitignore는 사용자 변경이므로 수정/stage/되돌리기 금지. 기존 릴리즈/태그 덮어쓰기 금지.

## 추가 수정 — 2026-11-02 (v1.11.2 배포 완료)
- 문서 탭 드래그 순서 이동 및 삽입 위치 표시. 기존 좌우 분할 이동과 선택·Undo 보존.
- 검색 결과 행의 세로 여백 축소. 글꼴 자체의 줄 높이는 보존.
- 도움말 본문 15px/24px 줄 간격, 문단 및 단축키 개행, 최소 폭 레이아웃 개선.
- 도움말 누락 보완: 탭 재정렬, 분할 메뉴, 최근 파일, 인코딩 재열기 실패, 결과 탭 Ctrl+W. 병합과 저장 관계 및 개발 용어 수정.
- Release 빌드 0경고/0오류. --result-font, --help-layout, --document-panes 통과. help-layout.png 시각 확인.
- 물리 마우스 테스트는 창 가림 때문에 실패할 수 있어 검증용 창만 Topmost 사용. GUI 테스트는 순차 실행.
- 50d80aa / v1.11.2로 EXE·ZIP 공개 완료: https://github.com/pubill13/MyTextEditor/releases/tag/v1.11.2 . 서버 SHA-256 일치 및 새 EXE 1.11.2 시작 확인. .gitignore 사용자 변경 유지.

## 이번 변경의 근거
- 인코딩 메뉴가 비어 있어 SubmenuOpened 자체가 발생하지 않던 구조: 초기 하위 항목 제공 후 열 때 실제 명령 생성.
- 줄바꿈 체크값이 native focus/status 갱신으로 Click 전에 되돌아갈 수 있어 편집기의 실제 상태를 기준으로 토글.
- Merge 다시 비교/중단/자동 표시와 자동 비교 옵션 제거. 편집·옵션 변경 후 자동 갱신, 내부 취소/오래된 결과 병합 차단/F5 유지.
- 탭바 우측 추가/삭제/변경 범례는 Scintilla 테마별 실제 line/inline 색과 일치.
- UI·툴팁·도움말은 파일 변경 모니터링으로 통일.

## 검증
- 솔루션 Release 빌드 0경고/0오류.
- --document-menu PASS: 실제 메뉴 OnClick, 줄바꿈 on/off 및 focus 갱신, 파일→인코딩 최초 열기, BOM 변경/원복.
- --document-display PASS: 인코딩-only 저장/BOM, Undo, 재열기 성공/실패/저장/버리기/취소/동시편집/닫기, 분할 자동 해제.
- --merge-110 PASS: 자동갱신, F5, 비활성 취소, 잠금, 지도, Undo/Redo.
- --merge-clarity PASS: 5테마, 900×560/1600×900, 범례색/탭 겹침/버튼/옵션 검증. artifacts/merge-layout-900.png와 1600.png는 WPF 배치 캡처이며 native 본문은 포함하지 않는다.
- 기존 Core42/42, --word-wrap, --file-sync 30/100/300MB, --result-font 통과 이력. 해당 로직 변경 없으면 반복 실행하지 않는다.
- 실제 DPI125/150% 전환과 물리 한글 IME 미검증.

## 실행 주의
- 네이티브 DLL 폴더 접근은 샌드박스에서 거부될 수 있으므로 권한 오류는 require_escalated로 재시도. 앱 코드 우회 금지.
- 사용자 기존 프로세스/미저장 창 임의 종료 금지. GUI 검증은 순차 실행.
- gh는 없음. 릴리즈는 artifacts/v1.11.4/publish-release.ps1에서 기존 git credential을 메모리 내 사용. 자격 증명 출력 금지. draft에 EXE/ZIP 업로드 후 서버 SHA-256 검증하고 공개한다.

## v1.11.3 (2026-11-02)
- 문서/검색 결과/Merge의 기존 다른 탭 닫기를 '이 창을 제외하고 모두 닫기'로 통일.
- 현재 문서 폴더 버튼 추가, 미저장 경로 없는 문서는 비활성화. 검색 경로는 명시적으로 버튼을 누를 때만 변경.
- Release 빌드, --folder-shortcut, --help-layout, --result-font 통과.
- --search-actions는 별도 결과 창의 물리 Ctrl+C 자동화 실패. 직접 실행한 Copy 명령은 통과. 원인 미확정, 복사 로직 변경 없음.
- 985b635 / v1.11.3 공개 완료. EXE/ZIP 서버 SHA-256 일치. https://github.com/pubill13/MyTextEditor/releases/tag/v1.11.3

## 문서 탭 새 창 분리 (v1.11.4 배포 완료)
- 사용자 선택: 별도 프로세스 대신 같은 프로세스의 독립 MainWindow. 문서 편집기 자체 이동으로 미저장 내용/인코딩/선택/Undo/줄바꿈 유지.
- 창 바깥 드롭으로 분리, Esc 취소. 기존 재정렬/좌우 분할 유지. 파일 열기·저장·닫기·인코딩 재열기 중 분리 차단.
- 이벤트 연결을 새 창으로 이전하며 이동 실패 시 원래 문서로 복구. 기존 검색 결과/비교는 이전 창에 남음.
- 종료는 마지막 창 기준. 파일 열기 IPC는 현재 남아 있는 창에 전달. 창별 글꼴/테마 리소스 분리.
- 최종 Release 빌드 0경고/0오류. --document-detach (물리 드래그 포함), --document-panes, --result-font, --help-layout PASS.
- 실제 혼합 DPI 다중 모니터 위치 검증은 미실시. 3128d9e / v1.11.4 공개 완료. EXE/ZIP 서버 SHA-256 일치 및 배포 EXE 정상 시작 확인. https://github.com/pubill13/MyTextEditor/releases/tag/v1.11.4

## 현재 작업 — 탭 왕복·종료 안정성·자동 복구 (미배포)
- 같은 프로세스의 기존 OmniEdit 창 위로 탭을 드롭하면 새 창을 만들지 않고 탭바 삽입 위치 또는 편집 영역의 해당 패널로 이동합니다. 창 밖에서만 새 창을 만듭니다.
- 이동 전후 네이티브 편집기와 이벤트 연결을 보존하고, 이동 실패 시 원래 창으로 롤백합니다. 예약 포커스·단축키·검색 결과 콜백은 세대와 문서 소유권을 다시 확인합니다.
- `%LOCALAPPDATA%\MyTextEditor\Recovery`에 원자적 복구본을 10초마다 기록합니다. 프로세스별 잠금, 손상·임시 파일 보존, 강제 종료 후 재실행 복구 선택, 저장·명시적 버리기 후 정리를 구현했습니다.
- Merge 저장·적용 완료가 닫힌 편집기나 저장 중 새 입력을 성공 처리하지 않도록 UI 스레드 복귀와 revision/resource 검사를 추가했습니다.
- Release 빌드 0경고/0오류. Core 테스트, `--recovery-store`, `--merge-lifetime`, `--document-detach`가 통과했으며 `--document-detach`는 실제 분리→재결합과 50회 왕복에서 같은 native editor·Undo를 확인합니다.
- 비정상 종료의 원인은 이벤트 로그와 30회 활성화 재현에서 확정되지 않았습니다. 확인된 문제는 이동 후 남는 논리 포커스와 stale callback이었고, 진단 로그는 예외 종류·스택·단계·소요 시간만 기록합니다. 이 변경은 아직 릴리즈하지 않습니다.
- 후속 검증에서 `Activate()`의 foreground 거절과 사용자 재선택을 구분했습니다. 현재 테스트는 제목 표시줄 실제 클릭 후 IsActive를 검증합니다. 50회 모두 실제 드래그 왕복, Undo, 최소화/복원, 빈 출발창 정상 Close, 마지막 창 종료 취소/변경 버리기를 통과했습니다. 검증용 EXE가 실제 WPF/Scintilla 창을 띄우며, 배포된 OmniEdit EXE의 시작·종료 전체 자동화와는 구분합니다.
- 30MB/300MB 기존 창 이동 각 10회 총 315.7/363.9ms, 핸들 증감 0. private memory 증감 각각 -1.8MB/+24.3MB로 장기 누적 여부를 이 표본만으로 확정하지 않습니다. 수정 전 DLL과 동일 조건 새 창 분리 단일 측정은 작은 문서 198.8→135.1ms, 30MB 202.6→165.9ms, 300MB 173.7→156.3ms. 측정 코드는 무시된 artifacts/transfer-benchmark에 있습니다.
- `--recovery-ui`는 주기 콜백, 변경 없는 캡처 생략, 캡처 중 편집/버리기, 백업 쓰기 실패 시 이동 차단, 모두 닫기 중 재게시 차단, 세션 복원·BOM·선택·Undo 미저장 상태·저장 후 삭제를 통과했습니다. `--recovery-store`의 overlapping Write→Discard 순서도 통과했습니다.
- 마지막 변경 이후 Release 0경고/0오류, `--document-display`, `--document-panes`(연속 Ctrl+W 포함), `--merge-lifetime`, `--help-layout` 통과. Core는 이번 작업 중 통과했습니다. 단축키 포커스 회귀는 IsActive의 일시적 상태 대신 기존 문서/포커스 세대로 판정해 수정했습니다.
- 남은 배포 기준은 사용자 비정상 종료 원인 입증과 혼합 DPI 확인입니다. 사용자 승인 계획에 따라 원인 미확정 상태를 해결 완료로 배포하지 않습니다. 소스 변경은 아직 커밋하지 않았고 `.gitignore` 사용자 변경은 보존했습니다.
- 2026-11-03 실제 Release OmniEdit.exe에서 합성 한글 미저장 문서 입력 → 자동 복구본 생성 → 해당 테스트 PID만 강제 종료 → 재실행 복구 안내 수락 → 본문/미저장/Undo 없음 확인 → 파일 저장 → 정상 종료까지 통과했습니다. 저장 파일 본문 일치와 해당 복구본 삭제도 확인했습니다. 앱 승인 시간 초과 문제는 이후 해소됐습니다.

- 300MB 문서의 기존 창 이동 1,000회(33.9초) 통과. 100회 간격 표본의 private memory 약 811~828MB, 핸들 780~785개로 누적 증가가 없었습니다. 초기 200회 대비 최종 메모리 +245,760바이트. 혼합 DPI는 현재 연결된 모니터가 1개라 미검증입니다.
- 추가 자원 조사에서 `InputManager → WinFormsAdapter → 해제된 ScintillaEditorHost → 닫힌 창` 참조를 덤프로 확인했습니다. 자식 컨트롤뿐 아니라 WindowsFormsHost.Dispose()도 호출하고 EditorFocused 연결을 비웁니다. 이동 중 편집기는 해제하지 않습니다. 논리 부모를 의심해 시도한 XAML 변경은 근거가 없어 원복했습니다.
- `--document-window-stress`는 50회 새 창 분리/출발창 정상 종료와 별도로 생성·해제한 편집기 호스트 10개의 GC 수집을 확인합니다. 테스트의 지역 변수 수명 영향은 NoInlining 별도 메서드로 제거했습니다. 마지막 결과는 닫힌 창 1개(접근성 COM 참조 경로), 핸들 +5이며 허용 기준을 통과했습니다. 진단용 장시간 대기 코드는 제거했습니다.
- Merge 창 자체의 clean/preapproved 정상 Close 회귀를 `--merge-lifetime`에 추가해 통과했습니다. Closing 중 Hide가 실패한다는 가설은 재현되지 않아 해당 제품 코드는 수정하지 않았습니다. 이전 지연 저장/적용 검증도 통과했습니다.
- 호스트 해제 수정 이후 최종 Release 빌드 0경고/0오류, --document-window-stress, --document-panes, --merge-lifetime, --document-detach, --recovery-ui 통과. 마지막 드래그 왕복 50회/Undo·Redo/출발창 정상 종료 통과, 30MB·300MB 이동 각 10회 313.2/317.1ms와 핸들 증감 0을 확인했습니다. 원래 사용자의 비정상 종료와 위 자원 누수의 인과관계는 입증하지 못했으므로 미배포 상태를 유지합니다.

## v1.11.5 배포 진행 (2026-11-03)
- 사용자가 남은 검증을 최소화하고 알려진 제약을 포함해 배포하도록 최신 지시했습니다. 이전 배포 보류 결정을 대체합니다. 비정상 종료 직접 원인 미확정·혼합 DPI 미검증은 릴리즈 노트에 명시합니다.

- 배포 완료: 9bc0e5e / v1.11.5, https://github.com/pubill13/MyTextEditor/releases/tag/v1.11.5 . Windows x64 자체 포함 EXE(파일 버전 1.11.5.0)·ZIP 업로드 및 서버 SHA-256/크기 일치 확인 후 공개했습니다. 알려진 제약은 릴리즈 노트에 포함했습니다. 사용자 .gitignore는 커밋하지 않았습니다.
