# 비교·병합 문제 개선 — 2026-11-09

상태: 구현과 최종 실제 조작·회귀 검증 완료. 기존 [탐색 보고서](merge-exploration-2026-11-09.md)의 제품 코드에 다음 개선을 적용했다.

최신 Release 빌드(경고 0, 오류 0), 코어 **43/43**, 개선·회귀 시나리오 **103/103**을 통과했다. 기존 Merge 5묶음, 일반 편집기 단축키와 도움말도 통과했다. 사용자의 PC 조작 중에는 실제 입력을 보류했고, 재개 요청 뒤 최종 코드를 전체 검증했다.

| 최종 검증 | 결과 |
|---|---|
| 병합·검색·키보드·저장·레이아웃·탭·추가 경계 | 81/81 |
| 원본 재로드·부분 취소·클립보드·겹친 저장 | 7/7 |
| 실제 MainWindow 문서와 연결한 흐름 | 15/15 |
| 코어 테스트 | 43/43 |
| 기존 Merge 검증 | scroll / lifetime / omni / clarity / 110 모두 종료 코드 0 |
| 일반 편집기 단축키 | 기존 24개 매핑 및 WPF 입력 포커스 보호 통과 |
| 도움말 | 검색·목차·Light/Dark 최소폭 통과 |

최종 일괄 검증 11단계는 모두 종료 코드 0이었으며 실행 시간 합계는 약 3분 31초였다. [전체 시나리오 결과](verification/v1.11.12/final-results.json), [11단계 실행 기록](verification/v1.11.12/validation.json), [검증 소스·어셈블리 SHA-256](verification/v1.11.12/build-manifest.json)을 보존했다. 최종 검증 뒤 해당 소스와 어셈블리의 해시가 같음을 다시 확인했다.

| 발견한 문제 | 개선 내용 | 재현·회귀 사례 |
|---|---|---|
| 원본 재로드 후 오래된 비교로 잘못 병합 | 비교·인라인 계산 취소, 세대 갱신, 본문·인덱스·표시 캐시 무효화. 취소된 이전 캡처가 캐시를 다시 채우지 않도록 완료 시 토큰·세대·revision 검증 | SOURCE-RELOAD-CACHE, SOURCE-RELOAD-MERGE-SAFETY |
| 일부 원본만 재로드하고 나머지 취소 | 이미 갱신한 본문에 맞는 비교를 재개하고 거절한 쪽의 미저장 내용 유지 | SOURCE-RELOAD-PARTIAL-CANCEL |
| 탭 전환 뒤 숨겨진 탭으로 입력 | 이전 native 포커스를 즉시 끊고 새 탭의 마지막 편집기로 이동. 빠른 전환·검색 입력·창 재활성화 확인 | T01, T-tab-type, T-three-tabs, T-pending-search, T-reactivate |
| 좌우 교환 시 잠금 전파 | 체크박스 UI 갱신과 모델 변경 분리, 변경한 쪽에만 잠금 적용 | T-lock-swap-* |
| 치환 버튼 Enter 불가 | 입력란의 Enter만 별도로 처리하고 버튼의 기본 실행 보존 | S-button-enter |
| 치환 입력란 Enter | Enter 현재 항목 치환, Shift+Enter 이전 찾기. 반복 키 입력 후 입력란 포커스 유지 | S-enter, S-repeat-enter, S-find-enter-focus |
| 동일 내용 전체 복사의 불필요 dirty/Undo | 실제 텍스트가 같으면 교체하지 않고 기존 미저장 상태와 Undo 이력 유지 | WF-15-IDENTICAL-COPY-DIRTY, U-noop-dirty |
| 클립보드 잠금 안내 불일치 | 실제 잠금 상태에 맞춰 편집 가능/읽기 전용 안내 갱신 | CLIPBOARD-LOCK-METADATA |
| 빈 반대쪽으로 전체 복사 불가 | 비교 결과 없이도 준비된 소스에서 복사하고 빈 쪽을 편집 버퍼로 승격. 대상 잠금과 줄바꿈·Undo 유지 | U-empty-target, U-copy-empty-*, U-copy-locked-empty |
| 검색 대상 드롭다운 Alt+↓ 간섭 | WPF 검색 대상의 기본 Alt 키 처리를 보존하고 포커스 없는 native 편집기의 단축키 가로채기 차단 | S-combo-keys, --editor-shortcuts |

치환할 일치 항목을 아직 선택하지 않았다면 첫 실행은 다음 일치를 선택하고 다음 실행에서 치환한다. README·도움말·툴팁에 이 동작을 안내한다.

## 검증 방법

수정 전 결과는 `artifacts/merge-exploration/`에 보존하며 수정 후 결과는 `artifacts/merge-exploration-fixed/`에 기록한다. 테스트는 기존 기대를 유지하고, 자동 비교의 디바운스가 앞선 요청을 취소할 수 있으므로 실제 최신 비교 결과가 준비될 때까지 기다린다.

최종 검증은 탐색 81개, 원본·저장 7개, 메인 창 흐름 15개로 총 103개다. 코어 43개, 기존 Merge 5묶음, 일반 편집기 단축키 처리, 도움말 검증도 함께 실행했다. 실제 마우스·키보드 검증은 사용자 요청으로 재개한 별도 구간에서 진행했다.

반복 줄 병합 검증 중 디바운스가 앞선 비교 요청을 취소했는데 검증기가 이를 완료된 결과로 간주하던 문제는 실제 최신 결과를 기다리도록 고쳤다. 검색 대상 콤보박스의 Alt 키는 두 편집기 모두 포커스가 없는 상황에서도 WPF/WinForms 전처리가 기본 동작을 막는 것을 확인하여 검색 영역의 Preview 단계에서 직접 처리한다. 단순히 특정 편집기의 단축키 가드만으로 해결됐다고 판단하지 않고 실제 콤보박스 조작까지 검증했다.

```powershell
dotnet build MyTextEditor.sln -c Release --no-restore -p:NuGetAudit=false
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --merge-exploration
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --merge-source-exploration
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --merge-workflow-exploration
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --editor-shortcuts
```

파일 선택·다른 이름 저장 대화상자의 전체 실기, 한국어 IME 조합, 혼합 DPI, 300MB 전체 비교는 이 검증의 범위에 포함하지 않는다. 위 검증 시점에는 버전 변경·커밋·릴리즈 배포를 수행하지 않았다. 후속 배포는 HANDOFF.md에 기록한다.
