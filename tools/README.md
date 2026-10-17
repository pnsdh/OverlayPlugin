# 개발 도구 (포크)

일반 사용자에게는 필요 없고 배포 zip에도 들어가지 않는, 개발할 때 쓰는 도구들입니다.

| 도구 | 하는 일 |
| --- | --- |
| [RenderBench](RenderBench/README.md) | 렌더러별(CEF CPU·GPU·공유 텍스처, WebView2) CPU 사용량과 실제 화면에 나간 움직임의 매끄러움 측정 |
| [WebView2Smoke](WebView2Smoke/README.md) | ACT 없이 오버레이 창을 띄워 입력·포커스·클릭 통과·복구 시험 |
| `RenderBench/gpu-kill.ps1` | GPU 프로세스를 8번 강제로 꺼도(한컴 업데이터 상황) 버티는지 시험 |
| `build-install.ps1 -Version <설치본 4자리>` | 빌드 → 릴리즈와 같은 구성으로 묶기 → (ACT가 꺼져 있으면) 설치·해시 대조. `-NoInstall`은 묶기까지만 |
| `HtmlRenderer/LatencyProbe.cs` | 실제 ACT에서 로그 줄이 오버레이에 닿기까지의 지연 측정 (아래) |
| `strip-clientstructs.ps1`, `fetch_deps.ps1` 등 | 빌드 준비 (본가) |

cactbot 쪽 도구(오버레이 부하 재생, 효과 비교, 설정 화면 회귀 시험)는 cactbot 포크의 `dev/`에 있습니다.

## 지연 측정 (LatencyProbe)

평소에는 꺼져 있습니다. ACT를 시작할 때 `%TEMP%\op-probe.enable` 파일(내용 없음)이 있으면 켜져,
`%TEMP%\op-latency.log`에 1분마다 요약(개수, 중앙값, 상위 10%·1%, 최댓값, 16·50·100·250ms 넘은 횟수)을 남깁니다.

| 항목 | 뜻 |
| --- | --- |
| `line time -> OverlayPlugin` | 로그 줄 시각부터 OverlayPlugin이 받기까지 (ACT·FFXIV 플러그인 처리) |
| `wv2 ui-thread wait [오버레이]` | WebView2 오버레이로 보낼 이벤트가 ACT 화면 스레드를 기다린 시간 |
| `ui thread tick (50ms timer)` | ACT 화면 스레드의 50ms 타이머 간격 (늘어난 만큼 그 스레드가 막혔던 것) |
| `[오버레이] [latency] ...` | cactbot 레이드보스 페이지가 직접 잰 도착 지연 (주소에 `&devProbe=latency`) |

타이머 항목은 Windows 타이머 간격(15.6ms) 때문에 문제가 없어도 약 12ms씩 늦게 나옵니다.
다 쓰면 파일과 주소 옵션을 지우세요.
