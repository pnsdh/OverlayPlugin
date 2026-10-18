# WebView2Smoke

ACT 없이 OverlayPlugin의 오버레이 창(`OverlayForm`)을 띄워, WebView2·CEF 렌더러의 동작을 시험하는 도구입니다.
입력(마우스·키보드·한글 조합), 포커스, 클릭 통과 판정, 프로세스가 죽었을 때의 복구 같은 것을 실제 창으로 확인합니다.

OverlayPlugin 솔루션에는 포함되지 않는 별도 프로젝트이고, 화면에 창이 뜹니다(게임 중에는 돌리지 마세요).

## 빌드

먼저 저장소 루트에서 OverlayPlugin을 빌드해 `out\Release`를 만든 뒤:

```powershell
cd tools\WebView2Smoke
dotnet build -c Release
```

`OP_OUT` 환경 변수로 다른 빌드 폴더를 쓸 수 있습니다.

## 사용법

```text
bin\Release\net48\WebView2Smoke.exe <페이지 URL> <로그 파일>
```

환경 변수 없이 돌리면 오버레이 하나를 띄우고 0.2초마다 이벤트를 보내며, 콘솔·로드 상태를 로그에 남깁니다.
시험은 환경 변수로 고릅니다(이 폴더의 `*.html`이 각 시험용 페이지).

| 환경 변수 | 시험 |
| --- | --- |
| `SMOKE_APPDATA` | WebView2·CEF 데이터 폴더 (실제 ACT 폴더와 섞이지 않게 꼭 따로 지정) |
| `SMOKE_PHASE2=1` | 클릭 통과 판정(투명한 곳은 통과), 더블클릭, 커서, 스크린샷 |
| `SMOKE_HITLOOP=1` | 커서를 올려 둔 동안의 판정 비용 |
| `SMOKE_EDGE=<배율>` | 소수 배율 확대에서 창 가장자리의 판정 |
| `SMOKE_WHEEL=1` | 마우스 휠(세로, Shift+가로) |
| `SMOKE_KEYS=1` | 입력칸 키보드 입력 |
| `SMOKE_IME=1` (`SMOKE_IME_BS`) | 한글 조합이 포커스를 잃을 때 유지되는지 |
| `SMOKE_FOCUS=1`, `SMOKE_COPY=1` | 입력칸 자동 포커스와 게임 창으로 돌려주기, 복사 |
| `SMOKE_KILL=1` | WebView2 브라우저 프로세스가 죽은 뒤 페이지가 돌아오는지 |
| `SMOKE_TDR=<폴더>` | GPU 옵션의 CEF 오버레이가 그래픽 드라이버 재시작 뒤에도 그리는지 |
| `SMOKE_LAZY=<폴더>` | CEF 오버레이가 생기기 전까지 CEF가 시작되지 않는지 |
| `SMOKE_CTRL=lazy\|eager` | ACT 설정 탭의 패널(OverlayControl)이 그려지는지 |
| `SMOKE_MIXED=1` (`SMOKE_MIXED_AT`) | WebView2 오버레이가 나중에 CEF가 시작될 때 멈추지 않는지 |
| `SMOKE_CEFCACHE` | CEF 캐시 폴더 |

`silent_server.py`는 접속만 받고 답하지 않는 서버로, 페이지가 로드를 시작하지 못하는 상황을 만듭니다.

로그·캡처·데이터 폴더는 이 폴더의 `out\` 아래에 두면 커밋되지 않습니다.
