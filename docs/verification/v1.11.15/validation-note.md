# v1.11.15 배포 검증

기능 구현 단계의 측정·회귀 결과는 ../merge-responsiveness-2026-11-11.md에 있다. 버전 변경 후 실행한 10개 단계(build, core, merge-window-order, diff-highlight-cache, merge-scroll, merge-independent, merge-lifetime, merge-110, editor-shortcuts, word-wrap)는 모두 종료코드 0이었다. 창 검증은 실제 클릭·Alt+Tab을 포함한 12/12 PASS, Core는 43/43, 빌드는 경고·오류 0개다.

그 뒤 배포 스크립트가 PowerShell JSON 배열을 중첩한 탓에 창 결과 개수를 1개로 읽어 멈췄다. 원본 결과는 12개 모두 PASS였으며, 배열 읽기를 고치고 같은 결과를 확인했다. 제품 결함이나 테스트 실패는 아니며 회귀 테스트를 다시 실행하지 않았다.

사용자가 이미 마친 검증의 반복 중단을 요청하여 추가 실행 테스트를 중단했다. 이후 작업은 자체 포함 EXE 생성, 파일 버전 확인, ZIP 내 EXE 해시 대조와 업로드 무결성·공개 상태 확인으로 제한했다. 최종 단일 EXE의 별도 실행 테스트는 하지 않았다. validation.json은 실제 수행한 10개 단계만 기록한다.
