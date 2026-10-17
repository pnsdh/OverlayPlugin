# PatchCheck

게임 패치 때 OverlayPlugin과 cactbot 플러그인이 메모리를 찾는 데 쓰는 시그니처가 새 게임에서도 맞는지
확인하기 위한 도구입니다. OverlayPlugin 솔루션에는 포함되지 않는 별도 프로젝트(.NET 10)입니다.

## 사용법

```powershell
dotnet run --project tools\PatchCheck -c Release -- <명령> [옵션]
```

| 명령 | 하는 일 |
| --- | --- |
| `list [--json <파일>]` | 시그니처를 소스에서 읽어 이름, 오프셋, RIP 여부, 패턴을 보여 줍니다. `--json`은 ActPluginRepack CLI의 `sigscan` / `sigregen --patterns`용 패턴 파일(`out\` 아래면 커밋 안 됨). |
| `scan [--exe <ffxiv_dx11.exe>] [--region Ko\|Intl\|Cn\|Tc]` | 게임 exe에서 각 시그니처가 몇 곳에 맞는지(코드 섹션 / 플러그인처럼 전체 섹션), 읽은 값이 어느 섹션을 가리키는지, 플러그인의 64KB 단위 읽기에 걸려 잘리는지. 판정은 실제로 쓰이는 것만: OverlayPlugin 각 읽기 클래스의 최신 버전(클래스 번호 655 = 6.55, 74 = 7.4), cactbot은 exe 지역 파일. 기본 exe는 한국 클라이언트. 종료 코드 2 = 깨진 것 있음. |
| `regen --old <exe> --new <exe> [--region ...]` | 옛 exe에서 맞던 시그니처를 새 exe용으로 다시 만듭니다(그대로 맞으면 `same`, 새로 만들면 `NEW`와 고칠 소스 파일, 못 만들면 `FAIL`). |

- 시그니처 출처: OverlayPlugin `OverlayPlugin.Core/MemoryProcessors`의 버전별 클래스, cactbot `plugin/CactbotEventSource/FFXIVProcess{Intl,Ko,Cn,Tc}.cs`. cactbot 폴더는 기본으로 이 저장소 옆 `..\cactbot`(`--cactbot`으로 지정).
- 스캔·재생성은 ActPluginRepack 저장소의 `lib\FFXIV.Signatures` 라이브러리를 씁니다. 저장소가 이 저장소 옆에 있어야 하고, 다른 곳이면 `dotnet build -p:ActPluginDir=<경로>\`.
- 옛 exe는 ActPluginRepack CLI의 `fetch-exe --region korean --version <버전>`으로 받을 수 있습니다.

어떤 상수를 어떤 오프셋으로 스캔하는지는 `Signatures.cs`의 표에 있습니다(스캔 자체는 부모 클래스에 있어서).
소스에 표가 모르는 스캔이나 새 버전 클래스(예: `CombatantMemory80.cs`)가 생기면 `WARN`으로 알려 줍니다. 그때 표에 추가하세요.

**한계**: 시그니처가 한 곳에 맞고 데이터 섹션을 가리켜도 엉뚱한 변수일 수 있습니다. 예: 7.57에서 옛 `JobGauge70` 시그니처는
여전히 한 곳에 맞지만, 쓰이는 `JobGauge74`와 다른(옛) 주소를 가리킵니다. 최종 확인은 게임 중 값으로 합니다.

## 시그니처로 찾지 않는 것

- 파티 정보: FFXIV_ACT_Plugin의 시그니처 관리자를 빌려 씁니다(ActPluginRepack이 점검하는 범위).
- 화면 요소(AtkStage): 버전별 고정 주소표를 씁니다. 패치 때 따로 확인해야 합니다.
- 구조체 오프셋(캐릭터 필드 위치 등): 시그니처가 맞아도 바뀔 수 있습니다. 게임 중 값 비교나 로그 검진
  (cactbot `dev/patch-health.mjs`)으로 확인합니다.
