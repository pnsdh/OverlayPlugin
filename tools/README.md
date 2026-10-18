# 개발 도구 (포크)

일반 사용자에게는 필요 없고 배포 zip에도 들어가지 않는, 개발할 때 쓰는 도구들입니다.

| 도구 | 하는 일 |
| --- | --- |
| [RenderBench](RenderBench/README.md) | 렌더러별(CEF CPU·GPU·공유 텍스처, WebView2) CPU 사용량과 실제 화면에 나간 움직임의 매끄러움 측정 |
| [WebView2Smoke](WebView2Smoke/README.md) | ACT 없이 오버레이 창을 띄워 입력·포커스·클릭 통과·복구 시험 |
| `UiShot` | 창 없이 설정 화면(일반·이벤트·웹소켓 탭, 오버레이 목록, 오버레이별 설정, 새 오버레이·이름 바꾸기·설정 오류 창)을 한국어·영어로 그려 `UiShot/out/`에 PNG로 저장, 옵션 위치·잘림 확인. `UiShot.exe techsupport [오버레이 추가 수]`는 기술 지원 정보 예시를 만들어 디스코드 글자 수(2000자) 안에 드는지 확인 |
| `TtsBench` | ACT 없이 플러그인 TTS 시험: 알림 문장 바꾸기 결과, 요청부터 재생까지 걸린 시간(처음/만들어 둔 음성), 겹칠 때 규칙(더 중요한 알림이 끊기, 반복 무시). 기본은 소리 없이(`--volume`으로 소리), `--trace`는 재생 시각. `--supertonic`: 실제로 다운로드해(해시 확인) 한국어 문장 생성 시간·CPU·메모리 측정 후 지움(`--keep`이면 남김, 설치된 모델과 같은 폴더라 주의), 들어 볼 파일은 `%TEMP%\supertonic-test.wav`. `--supertonic-speed`: 다운로드해 둔 모델로 스레드·품질별 속도(읽기만, 지우지 않음). `--requests`: 네트워크 없이 엔진별 요청 본문 확인 |
| [PatchCheck](PatchCheck/README.md) | 게임 패치 대응: OverlayPlugin·cactbot 메모리 시그니처를 새 게임 exe에서 점검(`scan`)하고 다시 만들기(`regen`). ActPluginRepack의 `FFXIV.Signatures` 라이브러리 사용 |
| `RenderBench/gpu-kill.ps1` | GPU 프로세스를 8번 강제로 꺼도(한컴 업데이터 상황) 버티는지 시험 |
| `missing_ko.py` | 한국어 번역(`*.ko-KR.resx`)에 빠졌거나 영어 그대로 남은 설정 화면 문구 목록. 화면 문구를 넣거나 바꾼 뒤 확인 |
| `build-install.ps1 -Version <설치본 4자리>` | 빌드 → 릴리즈와 같은 구성으로 묶기 → (ACT가 꺼져 있으면) 설치·해시 대조. `-NoInstall`은 묶기까지만 |
| `OverlayPlugin.Core/MemoryProcessors/MemorySelfCheck.cs` | 게임 패치 뒤 메모리 구조 자가 점검: 메모리에서 읽은 캐릭터 값을 FFXIV_ACT_Plugin 값과 필드별로 비교 (아래) |
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

## 메모리 구조 자가 점검 (MemorySelfCheck)

평소에는 꺼져 있습니다. ACT를 시작할 때 `%TEMP%\op-selfcheck.enable` 파일이 있으면 켜져, 게임 중 3초마다
OverlayPlugin이 메모리에서 읽은 캐릭터(이름, 종류, 소유자, 몬스터 ID, 위치, 방향, 시전, 직업, 레벨, HP/MP, 대상, 서버)를
FFXIV_ACT_Plugin의 값과 ID로 맞춰 비교하고, 대상과 직업 게이지도 플레이어와 맞는지 봅니다.
1분마다 `%TEMP%\op-selfcheck.log`에 필드별로 다른 비율을 남깁니다(`BROKEN?` = 절반 넘게 다름, `odd` = 10% 넘게).

- 오프셋이 밀린 필드는 거의 모든 캐릭터에서 다릅니다. 움직이는 캐릭터의 위치·방향은 두 쪽을 읽는 시점이 달라 조금은 늘 다릅니다.
- 이름은 값 없이 다름 여부만 남깁니다.
- 패치 직후엔 FFXIV_ACT_Plugin 쪽이 틀렸을 수도 있습니다. 값이 그럴듯한 쪽을 보세요.
- cactbot 플러그인도 같은 파일이 있으면 자기가 읽는 플레이어 정보를 같은 방식으로 비교해 같은 로그에 남깁니다.

