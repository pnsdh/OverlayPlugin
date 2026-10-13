# RenderBench

오버레이 페이지를 OverlayPlugin과 같은 창(테두리 없음·항상 위·클릭 통과)에 띄우고, 렌더러별로 다음을 잽니다.

* CPU: 이 프로그램과 하위 프로세스(CEF·WebView2 보조 프로세스 포함) 전체의 CPU 시간 (코어 1개 기준 %)
* 화면 표시: 실제 모니터에 나간 화면(DXGI Desktop Duplication)에서, 페이지에 넣은 움직이는 점이 화면 갱신마다 새 위치로 바뀌었는지

OverlayPlugin 솔루션에는 포함되지 않는 별도 프로젝트입니다.

## 빌드

```powershell
cd tools\RenderBench
dotnet build -c Release
```

`bin\Release\RenderBench.exe`가 생성됩니다.

## 사용법

```text
RenderBench <모드> <URL> <fps> <초> <로그 파일> [폭] [높이]
```

| 모드 | 내용 |
| --- | --- |
| `cef-cpu` | CEF 소프트웨어 렌더링 → UpdateLayeredWindow (OverlayPlugin 기본) |
| `cef-gpu` | CEF GPU 렌더링 후 CPU로 읽어 UpdateLayeredWindow |
| `cef-shared` | CEF GPU 렌더링 → 공유 텍스처 → DirectComposition (포크의 GPU 옵션) |
| `webview2` | 엣지 WebView2가 DirectComposition 영역에 직접 그림 (fps 설정 없음, 모니터에 맞춰 그림) |

창은 주 모니터의 (200, 200)에 뜹니다. 측정은 시작 5초 뒤부터이며, 끝나면 창을 닫고 로그를 남깁니다.

환경 변수:

| 변수 | 내용 |
| --- | --- |
| `RB_NOPROBE=1` | 움직이는 점과 화면 측정을 끕니다. 화면 측정 자체의 CPU를 빼고 비교할 때 씁니다. |
| `RB_CEFARGS="스위치=값;스위치2"` | CEF 명령줄 스위치 추가 |
| `RB_EXTBF=1` | CEF: 모니터 갱신마다 External BeginFrame 전송 |
| `RB_DUMP=<파일>` `RB_DUMP_AT=<프레임>` | `cef-shared`: 지정한 프레임을 BGRA 원본(앞 8바이트 폭·높이)으로 저장 |

## 로그 읽는 법

```text
RESULT mode=webview2 cpu=36.8% of one core (process tree)
display: refreshes=2697 new probe position on 99.9% of refreshes (...); step error mean=0.08px, off by more than half a refresh 0.1% of steps
```

* `new probe position on N% of refreshes`: 화면 갱신 중 새 장면이 표시된 비율. 100%에 가까울수록 끊김(같은 장면 반복)이 없습니다.
* `step error mean`: 갱신마다 점이 움직인 거리와, 그 사이 시간에 맞는 거리의 차이. 작을수록 움직임이 고릅니다.
* `off by more than half a refresh`: 그 차이가 갱신 반 번만큼의 움직임보다 큰 비율. 장면 시점이 들쭉날쭉하면 커집니다.

CPU는 측정마다 몇 %씩 흔들리므로 같은 조건을 여러 번 재서 비교합니다.
화면 측정은 실제 화면을 읽으므로, 측정 중에는 창 위로 다른 창을 올리지 않습니다.

테스트 페이지에 실제 게임 화면을 배경으로 쓰는 경우, 그 이미지는 저장소에 넣지 않습니다.
