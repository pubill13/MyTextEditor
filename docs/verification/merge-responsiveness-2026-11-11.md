# 비교/병합 독립 창·스크롤 검증 (2026-11-11)

이 기록은 구현 완료 시점의 검증이다. 후속 사용자 요청으로 진행하는 v1.11.15 배포 증빙은 docs/verification/v1.11.15/에 별도로 기록한다.

## 변경

- MainWindow가 비교/병합 창의 Owner를 지정하지 않는다. 저장 위치와 테마·원본 문서·도움말 콜백, 메인 종료 후 작업 유지 경로는 보존한다.
- 네이티브 입력 처리 직후 실제 위치를 확인하고, 상태 표시·스크롤바·강조 갱신 전에 대응 줄로 반대편을 이동한다. 기존 매핑·재진입 방지·끝 범위 제한은 유지한다.
- 프레임 단위 표시 갱신은 기존 Rendering 예약을 사용한다. 같은 문서 revision에서는 직전에 그린 marker handle과 inline 범위만 지운다. 편집·로드 시에는 전체 강조를 무효화한다.
- 현재 화면에서 벗어난 inline 작업을 취소하며, 최신 화면의 캐시 누락을 후속 계산한다. 스크롤 경로에서 캐시된 줄 텍스트를 사용한다.
- 설정·IPC·파일 형식과 버전은 변경하지 않았다. 릴리즈 배포는 수행하지 않았다.

## 측정 방법

Windows PC의 Release 구성에서 실제 Scintilla HWND에 Win32 휠·PageUp/Down·사용자 스크롤바 드래그 메시지를 전달했다. 실제 비교 엔진 결과를 사용하고 WndProc 반환 직후와 SendMessage 반환 직후 반대편 위치를 매핑 결과와 대조했다. 물리 마우스·키보드 조작 성능을 측정했다는 의미는 아니다.

각 자료에서 좌우 × 입력 3종 × 120회, 총 720회 측정했다. 자료는 12,000줄/4,210개 차이 블록과 30/100/300MB 동일 문서다. 대용량에서 차이가 많은 경우를 함께 대표하는 측정은 아니다. 비교 완료 후 스크롤을 측정하며 최초 비교 시간은 위치 전달 지연 기준에 포함하지 않는다.

수정 전 제품: commit `b75d4a7932fb68a96c5bc19594ae690f7a65fbef`, OmniEdit.dll SHA-256 `EB677FAF4E9DBA7DEC2CDE7ADEB910875D349FF07A6A756F3BC34BD38EEF8210`.

수정 전후 모두 같은 성능 runner DLL SHA-256 `75D5FD66CA6D703DA5A610BDDB0248120D3DD1AF3BF35099A14936FDE3412F5D`를 사용했다. 이후 창 검증의 SKIP 기록과 경계 회귀 테스트만 추가했다. 로컬 원시 측정은 `artifacts/merge-responsiveness/baseline-large.json`, `after-large.json`에 있다.

입력 처리 시간, 위치 통지 시간, native Refresh, 강조·화살표·지도 갱신, Dispatcher drain을 분리한다. 16ms 기준은 입력 처리 반환 시 대응 위치 도달과 위치 전달에 적용하며 전체 화면 그리기 시간을 보장하지 않는다. Dispatcher drain은 화면 프레임 대기도 포함할 수 있다.

## 결과

각 시간은 좌우·입력 6개 그룹의 p95 중 최대값이다. 지연 건수는 입력 반환 시 반대편이 대응 위치에 도달하지 못한 횟수다.

| 자료 | 입력 수 | 지연 전 → 후 | 수정 후 입력 처리 p95 | 표시 갱신 p95 전 → 후 |
| --- | ---: | ---: | ---: | ---: |
| 4,210개 차이 블록 | 720 | 480 → 0 | 0.719ms | 3.785 → 3.922ms |
| 30MB | 720 | 480 → 0 | 1.099ms | 1.304 → 0.020ms |
| 100MB | 720 | 480 → 0 | 0.892ms | 6.591 → 0.022ms |
| 300MB | 720 | 480 → 0 | 0.937ms | 11.112 → 0.013ms |

수정 전 휠·키보드 1,920/1,920회 지연을 재현했다. 스크롤바 드래그는 전후 모두 지연이 없었다. 수정 후 전체 2,880회에서 WndProc 반환·SendMessage 반환 시 위치 불일치, 위치 통지 누락, paint 후 위치 불일치가 모두 0건이었다. 위치 통지 p95 최대 1.060ms, 입력 처리 p95 최대 1.099ms로 16ms 기준을 충족했다. 전후 runner의 gate 실패는 16개 그룹 → 0개 그룹이다.

표의 표시 갱신은 RenderMergeGutter 안의 강조·화살표·지도 처리다. native Refresh와 Dispatcher drain까지 포함한 전체 처리 p95 최대는 차이가 많은 자료에서 40.790 → 46.120ms, 30MB에서 31.463 → 31.385ms, 100MB에서 66.363 → 31.379ms, 300MB에서 46.597 → 31.228ms였다. 따라서 모든 화면 그리기가 16ms 이내이거나 차이가 많은 자료의 전체 그리기가 빨라졌다는 결론은 내리지 않는다. 개선의 확인 대상은 같은 입력 주기의 대응 위치 도달과 문서 크기에 비례하던 강조 제거 비용이다.

실제 창 검증에서 수정 전 Owner 연결과 메인 최소화 시 Merge 숨김을 재현했다. 수정 후 12/12개 항목이 통과했다: 저장 위치, Owner 없음, 각 창의 실제 클릭 전면 전환, Alt+Tab, 양방향 독립 최소화·복원, 닫고 다시 열기 위치, 테마, 메인 종료 전후 도움말 소유 관계, 메인 종료 후 Merge 내용 유지. 수정 전 물리 클릭·Alt+Tab 3개 항목은 외부 창이 가려 SKIP으로 기록했으며, 수정 후 실행은 SKIP 없이 통과했다. 테스트에서 외부 창을 조작하지 않았다.

수정 후 제품 DLL SHA-256: `6412BB4E3411CBA5224477E7160AB47DEF4049E11C5EC0AD9B3F4201AEE81A7D`. 최종 창·회귀 runner DLL SHA-256: `3E6E57D0CBB65E5D341D738C9313E80F6E16BFA9074BEFD4F891F4585784B537`.

## 회귀 검증

- 전체 솔루션 Release 빌드: 경고 0, 오류 0. Core 콘솔 테스트: 43/43.
- `--diff-highlight-cache`: 이전 marker/indicator 제거, 현재 줄·사용자 indicator 보존, 선택·화면 위치 보존, Unicode/emoji/CRLF/빈 줄 매핑, 잘못된 범위 거절, 앞부분 삽입·삭제 후 초기화, revision 0 재사용·짧은 문서 재로드 통과.
- `--merge-scroll`: 대응 줄 매핑의 추가·삭제 구간 및 시작·끝 양방향 즉시 동기화, 가로/세로 ON/OFF와 범위 제한, 병합 후 위치 유지, Undo/Redo, merge-next, 재비교 중 사용자 스크롤 우선 처리 통과.
- `--merge-110`, `--merge-independent`, `--merge-lifetime`, `--merge-external-changes`, `--editor-shortcuts`, `--word-wrap` 통과. 자동 비교·비활성 탭 취소·오래된 결과 차단·테마·숨긴 창·원본 반영·저장·도움말·메인 종료·비동기 작업 중 편집/종료·외부 재로드·단축키·자동 줄바꿈을 확인했다.
- 별도 Reviewer가 최종 제품 diff를 검토했으며 추가 필수 수정 사항은 없었다. 성능 측정의 반복 프레임 갱신은 정상 종료했고 강조 갱신의 위치 변경·사용자 표시 훼손은 관찰되지 않았다. 깜빡임의 모든 시각적 조건까지 보장하는 검증은 아니다.
- 사용자 `.gitignore` SHA-256 `7305131CE0F3F8709EAB3E15035EA6F554D68AF7DA411D2A35CD86EB1B640D0A` 유지. 커밋·태그·원격 릴리즈 생성 없음.

## 재현 명령

```powershell
dotnet build tests/MyTextEditor.Performance -c Release --no-restore
dotnet run --project tests/MyTextEditor.Core.Tests -c Release --no-restore
$runner = './tests/MyTextEditor.Performance/bin/Release/net9.0-windows/MyTextEditor.Performance.exe'
& $runner --merge-responsiveness --large
& $runner --merge-window-order
& $runner --diff-highlight-cache
& $runner --merge-scroll
```

수정 전 비교는 사전에 보관한 baseline-bin의 제품 DLL에 동일한 runner 4개 파일만 복사하고 `--baseline --large`로 실행했다. baseline의 gate 실패는 문제 재현 기록이며 성공으로 해석하지 않는다.
