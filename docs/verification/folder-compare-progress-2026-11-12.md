# 폴더 비교 속도·진행 표시·목록 개선

## 변경과 정확도

- 디렉터리 열거에서 얻은 크기·수정 시각을 재사용하고, 파일 비교를 최대 4개씩 처리한다. 파일 내용은 기존처럼 바이트로 비교하며, 비교 후 파일 정보 재조회로 열거 이후의 변경도 오류로 처리한다.
- 목록 수집 단계에는 움직이는 진행 막대와 발견 개수, 비교 단계에는 처리/전체 개수와 비율을 표시한다. 큰 파일을 읽는 동안에는 현재 파일의 읽은 용량도 약 100ms 간격으로 전달한다.
- 취소·폴더 접근 실패 시 이전 결과를 유지하며, 재비교·종료된 작업의 늦은 알림을 무시한다. 개별 파일 오류는 결과 행에 표시한다.
- 양쪽에 수정 날짜(로컬 시간), 확장자 기반 형식, 읽기 쉬운 크기를 표시한다. 정확한 바이트 수는 행 툴팁에 표시하며 폴더 크기는 계산하지 않는다.
- 폴더 우선 자연스러운 이름순이 기본이다. 날짜·형식·크기는 클릭한 쪽의 값으로 정렬하며, 값이 없는 행은 그룹 끝에 둔다. 내림차순에서도 폴더 우선과 좌우 행 대응을 유지한다. 정렬은 탭별로 새로고침·필터 변경에도 유지한다.
- 현재 폴더만 비교하고 하위 폴더는 열 때 비교한다. 의존성 추가와 릴리즈 배포는 없다.

## 같은 환경에서 측정한 결과

수정 전후 각각 빈 파일 50,000쌍을 새 임시 폴더에 생성하여 3회 측정했다. 파일 생성·삭제는 측정 시간에 포함하지 않는다. 비교 시작부터 결과 바인딩·레이아웃 완료까지 측정하며, 20ms UI 타이머의 최대 실행 간격도 기록했다.

| 항목 | 수정 전 | 수정 후 |
| --- | ---: | ---: |
| 1회 비교·표시 | 28,107ms | 2,764ms |
| 2회 비교·표시 | 28,163ms | 2,752ms |
| 3회 비교·표시 | 28,556ms | 2,797ms |
| 비교·표시 중앙값 | 28,163ms | 2,764ms |
| UI 최대 실행 간격 범위 | 126–131ms | 211–233ms |
| 누적 관리 할당량 중앙값 | 1,499,603,552B | 176,190,208B |
| 최대 작업 집합 범위 | 193–195MB | 200–205MB |

빈 파일 검사 시간은 약 90.2% 줄었고, UI 최대 정지 시간은 1.5초 기준을 통과했다. 누적 할당량은 동시 점유 메모리가 아니다. 작업 집합과 UI 최대 간격은 소폭 늘었으며, 실제 디스크·네트워크 경로 및 파일 내용에 따라 결과는 달라진다.

추가 수정 후 검사:

| 자료 | 비교·표시 | UI 최대 간격 |
| --- | ---: | ---: |
| 1KiB 파일 1,000쌍 | 335ms | 223ms |
| 64MiB 파일 4쌍(한쪽 합계 256MiB) | 300ms | 85ms |

작은 파일·큰 파일 수치는 수정 후 단회 측정이며, 이전 버전 대비 개선율을 의미하지 않는다. 큰 파일은 길이를 설정하여 0으로 채워진 파일로 생성했다. 수정 후 원본 JSON과 화면 PNG는 `artifacts/folder-compare/`에 저장한다. 수정 전 3회 수치는 이번 실행 로그를 위 표에 기록했으며, 기존 검사기의 `stress-before.json`에는 마지막 회차만 남는다.

## 검증

- Release 솔루션 빌드: 경고·오류 0개.
- Core: 44/44 통과. 수정 시각 제공, 같은 크기·시각의 다른 내용, 열거 후 변경 감지, 단계별 진행과 최종 개수, 자연 정렬·큰 숫자, 기존 접근 오류·취소·스트리밍·할당량 검사 포함.
- `--folder-compare`: 날짜·형식·크기·이름·상태 양방향 정렬, 누락 값, 폴더 우선, 좌우 행 순서, 선택·스크롤·필터·새로고침, 진행 막대 시작·종료·취소, 폴더 접근 실패 시 이전 결과 유지 및 기존 폴더/파일 탭 작업 흐름 검증.
- `--folder-input`: Enter·따옴표 경로·드롭·혼합 입력 거부·기존 파일 버퍼 유지 회귀 통과.
- 밝은/어두운 테마, 로딩 중 표시, 최소 창 너비 캡처를 확인했다. 좁은 창에서는 가로 스크롤로 메타데이터를 확인한다. 실제 DPI 전환 검사는 수행하지 않았다.

```powershell
dotnet build MyTextEditor.sln -c Release --no-restore -p:NuGetAudit=false
dotnet run --project tests/MyTextEditor.Core.Tests -c Release --no-build --no-restore
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --folder-compare
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --folder-input
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --folder-stress --files=50000
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --folder-stress --files=1000 --fixture=small
dotnet run --project tests/MyTextEditor.Performance -c Release --no-build --no-restore -- --folder-stress --files=4 --fixture=large
```
