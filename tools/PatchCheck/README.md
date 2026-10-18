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
| `scan [--exe <ffxiv_dx11.exe>] [--region Ko\|Intl\|Cn\|Tc]` | 게임 exe에서 각 시그니처가 몇 곳에 맞는지(코드 섹션 / 플러그인처럼 전체 섹션), 읽은 값이 어느 섹션을 가리키는지, 플러그인의 64KB 단위 읽기가 놓치는지(일치와 읽을 값이 한 번 읽은 64KB 안에 다 들어와야 찾음). 판정은 실제로 쓰이는 것만: OverlayPlugin 각 읽기 클래스의 최신 버전(클래스 번호 655 = 6.55, 74 = 7.4), cactbot은 exe 지역 파일. 기본 exe는 한국 클라이언트. 종료 코드 2 = 깨진 것 있음. |
| `regen --old <exe> --new <exe> [--region ...] [--only <이름 일부>,...]` | 옛 exe에서 맞던 시그니처를 새 exe용으로 다시 만듭니다. `--only`는 이름으로 골라(옛 버전 클래스 포함) 지난 패치를 그때 쓰던 시그니처로 재현할 때 씁니다(아래 예행연습)(그대로 맞으면 `same`, 패턴은 맞는데 읽는 자리가 바뀌었으면 `MOVE`(offset 또는 ripOffset, 예: 비교 즉치가 1바이트에서 4바이트로), 새로 만들면 `NEW`와 고칠 소스 파일, 못 만들면 `FAIL`. call 대상을 비교하지 못한 경우는 `!` 줄로 표시). |

- 시그니처 출처: OverlayPlugin `OverlayPlugin.Core/MemoryProcessors`의 버전별 클래스, cactbot `plugin/CactbotEventSource/FFXIVProcess{Intl,Ko,Cn,Tc}.cs`. cactbot 폴더는 기본으로 이 저장소 옆 `..\cactbot`(`--cactbot`으로 지정).
- 스캔·재생성은 ActPluginRepack 저장소의 `lib\FFXIV.Signatures` 라이브러리를 씁니다. 저장소가 이 저장소 옆에 있어야 하고, 다른 곳이면 `dotnet build -p:ActPluginDir=<경로>\`.
- 옛 exe는 ActPluginRepack CLI의 `fetch-exe --region korean --version <버전>`으로 받을 수 있습니다.

어떤 상수를 어떤 오프셋으로 스캔하는지는 `Signatures.cs`의 표에 있습니다(스캔 자체는 부모 클래스에 있어서).
소스에 표가 모르는 스캔이나 새 버전 클래스(예: `CombatantMemory80.cs`)가 생기면 `WARN`으로 알려 줍니다. 그때 표에 추가하세요.

**한계**: 시그니처가 한 곳에 맞고 데이터 섹션을 가리켜도 엉뚱한 변수일 수 있습니다. 예: 7.57에서 옛 `JobGauge70` 시그니처는
여전히 한 곳에 맞지만, 쓰이는 `JobGauge74`와 다른(옛) 주소를 가리킵니다. 최종 확인은 게임 중 값으로 합니다.

## 시그니처로 찾지 않는 것

- 파티 정보: 주소는 FFXIV_ACT_Plugin의 시그니처 관리자를 빌려 씁니다(ActPluginRepack이 점검하는 범위). 구조는 자가 점검이 FFXIV_ACT_Plugin의 파티 구분과 비교합니다.
- 화면 요소(AtkStage): 버전별 고정 주소표를 씁니다. 패치 때 따로 확인해야 합니다.
- 구조체 오프셋(캐릭터 필드 위치 등): 시그니처가 맞아도 바뀔 수 있습니다. 게임 중 값 비교나 로그 검진
  (cactbot `dev/patch-health.mjs`)으로 확인합니다.

## 예행연습 기록 (2026-10-18)

옛 exe(글로벌, ActPluginRepack `fetch-exe --patch <패치 파일 버전> --out <파일>`)로 지난 패치를 재현한 결과입니다.

| 패치 | 결과 | 본가가 실제로 한 일 |
| --- | --- | --- |
| 7.45h2 → 7.5 → 7.51 → 7.51h (2026.03.17 → 04.21 → 05.25 → 06.10) | 쓰이는 12개 모두 `same` | 시그니처 변경 없음. 대신 캐릭터 구조체의 시전 플래그 두 개가 한 바이트로 합쳐짐(6주 뒤 발견, PR #540) → 시그니처 점검으로는 못 잡고 자가 점검 몫 |
| 7.25h3 → 7.30 (2025.06.28 → 07.25) `--only InCombatMemory70` | 옛 시그니처가 5곳에 맞음 → `NEW 74??803D??????????74??488B03488BCBFF50 offset -15 ripOffset 1` | 본가 `InCombatMemory73`과 똑같은 패턴 |
| 같은 패치 `--only EnmityHudMemory70` | `FAIL` (옛 위치를 담은 함수를 새 exe에서 못 찾음) | 본가는 다른 코드 위치로 새 시그니처를 만듦 |
| 7.38 → 7.4 (2025.10.30 → 12.09) cactbot `Intl.kJobData`(옛 소스를 `--cactbot`으로) | `scan`은 `FAIL`로 잡음, `regen`은 `FAIL` | 본가는 다른 코드 위치(`488D0D????????0F95C2E8????????488B8D`)로 바꿈 |

정리: `scan`은 깨진 것을 다 잡았고, `regen`은 같은 함수가 남아 있으면 본가와 같은 답을 냅니다. 함수째 바뀌면 `FAIL`이고, 그때는
수동으로(옛 exe에서 그 시그니처가 가리키던 변수를 새 exe에서 다른 코드가 참조하는 곳을 찾아) 만들거나 본가 대응을 기다립니다.
