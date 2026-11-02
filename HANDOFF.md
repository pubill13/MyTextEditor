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
- gh는 없음. 릴리즈는 artifacts/v1.11.2/publish-release.ps1에서 기존 git credential을 메모리 내 사용. 자격 증명 출력 금지. draft에 EXE/ZIP 업로드 후 서버 SHA-256 검증하고 공개한다.
