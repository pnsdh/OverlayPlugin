# PatchCheck

게임 패치 때 OverlayPlugin과 cactbot 플러그인이 메모리를 찾는 데 쓰는 시그니처가 새 게임에서도 맞는지
확인하기 위한 도구입니다. OverlayPlugin 솔루션에는 포함되지 않는 별도 프로젝트(.NET 10)입니다.

## 사용법

```powershell
dotnet run --project tools\PatchCheck -c Release -- list [--cactbot <cactbot 폴더>] [--json <파일>]
```

- `list`: 시그니처를 소스에서 읽어 이름, 오프셋, RIP 여부, 패턴을 보여 줍니다.
  - OverlayPlugin: `OverlayPlugin.Core/MemoryProcessors`의 버전별 클래스(캐릭터 목록, 대상, 적대감, 전투 상태, 직업 게이지 등)
  - cactbot: `plugin/CactbotEventSource/FFXIVProcess{Intl,Ko,Cn,Tc}.cs`(캐릭터 목록, 직업 게이지, 전투 상태)
  - cactbot 폴더는 기본으로 이 저장소 옆의 `..\cactbot`입니다.
- `--json`: ActPluginRepack CLI의 `sigscan` / `sigregen --patterns`가 읽는 패턴 파일
  (`{ name, pattern, syntax: "hex", offset, rip, ripOffset }`)로 씁니다. `out\` 아래에 두면 커밋되지 않습니다.

어떤 상수를 어떤 오프셋으로 스캔하는지는 `Signatures.cs`의 표에 있습니다(스캔 자체는 부모 클래스에 있어서).
소스에 표가 모르는 스캔이나 새 버전 클래스(예: `CombatantMemory80.cs`)가 생기면 `WARN`으로 알려 주고 종료 코드 2를 냅니다.
그때 표에 추가하세요.

## 시그니처로 찾지 않는 것

- 파티 정보: FFXIV_ACT_Plugin의 시그니처 관리자를 빌려 씁니다(ActPluginRepack이 점검하는 범위).
- 화면 요소(AtkStage): 버전별 고정 주소표를 씁니다. 패치 때 따로 확인해야 합니다.
- 구조체 오프셋(캐릭터 필드 위치 등): 시그니처가 맞아도 바뀔 수 있습니다. 게임 중 값 비교나 로그 검진
  (cactbot `dev/patch-health.mjs`)으로 확인합니다.

## 예정

ActPluginRepack 저장소의 시그니처 라이브러리(중립 이름, 예정 `FFXIV.Signatures`)가 나오면 이 도구가 직접 참조해,
새 게임 exe에서 각 시그니처가 몇 군데 맞는지, 찾아간 주소가 그럴듯한 섹션인지, 64KB 경계에 걸리는지까지 보고하고,
옛 exe와 새 exe로 시그니처를 다시 만드는 기능을 붙일 예정입니다.
