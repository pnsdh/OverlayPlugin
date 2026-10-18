# OverlayPlugin (한국어 개인 포크)

[![최신 릴리즈](https://img.shields.io/github/v/release/pnsdh/OverlayPlugin?color=brightgreen&sort=semver)](https://github.com/pnsdh/OverlayPlugin/releases/latest)

[OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin)의 오버레이 표시 성능을 손본 포크입니다. 본가 업데이트는 자동으로 반영됩니다.
[cactbot 한국어 포크](https://github.com/pnsdh/cactbot)와 함께 쓰는 것을 기준으로 합니다.

* 오버레이를 엣지 엔진(WebView2)으로 그려, 타임라인처럼 계속 움직이는 오버레이가 끊김 없이 부드럽습니다.
* 브라우저 엔진(CEF) 152, GPU로 그리기, 최대 프레임 레이트 480을 지원합니다. (본가는 CEF 95, 60fps)

문의·버그 제보는 [디스코드](https://discord.gg/R75tq68bA9)로 해 주세요.

## 설치

ACT, FFXIV ACT Plugin, 본가 OverlayPlugin이 설치된 상태에서 OverlayPlugin 폴더만 바꿉니다. 오버레이와 설정은 그대로 유지됩니다.

1. ACT를 종료합니다.
1. [최신 릴리즈](https://github.com/pnsdh/OverlayPlugin/releases/latest)에서 `OverlayPlugin-버전.zip`을 다운로드합니다.
   파일을 마우스 오른쪽 버튼으로 클릭 → **속성**에 **차단 해제**가 있으면 체크합니다.
1. ACT 폴더(보통 `C:\Program Files (x86)\Advanced Combat Tracker`)의 `Plugins\OverlayPlugin` 폴더를 ACT 폴더 밖으로 옮겨 둡니다. 되돌릴 때 씁니다.
1. 압축을 풀어 나온 `OverlayPlugin` 폴더를 같은 자리에 넣습니다. 관리자 권한 확인 창이 뜨면 **계속**을 누릅니다.
1. ACT를 실행합니다. 처음에는 새 브라우저 엔진(CEF)을 다운로드하니 진행 창이 닫힐 때까지 기다립니다.

경로가 같으므로 ACT에 플러그인을 다시 추가할 필요는 없습니다.

## 부드럽게 쓰기

오버레이는 기본으로 WebView2로 그립니다. 이 버전으로 처음 업데이트하면 기존 오버레이도 한 번 WebView2로 옮겨지고, 그 뒤에 CEF로 되돌린 것은 그대로 둡니다.
cactbot 타임라인·알람처럼 계속 움직이는 오버레이에 특히 효과가 큽니다.
오버레이마다 `고급`(`Advanced`) 탭의 `렌더러`에서 **`WebView2로 그리기`** 를 끄면 원래 엔진(CEF)으로 돌아갑니다. 바로 적용됩니다.
플러그인 설정 화면(cactbot 설정 등)도 기본으로 WebView2로 그립니다(`일반` 탭에서 끌 수 있고, ACT를 다시 시작하면 적용).

<details>
<summary>WebView2로 그리기 자세히</summary>

* 엣지 엔진이 모니터 갱신에 맞춰 직접 그려, CEF보다 움직임이 고르고 미세한 끊김이 없습니다.
  그래서 `최대 프레임 레이트`와 `모니터에 맞춤`은 쓰이지 않아 비활성화됩니다.
* 클릭, 마우스 휠, 투명한 부분 클릭 통과, 키보드 입력(한글 포함)은 CEF와 같이 동작합니다.
  글 입력칸을 누르면 그동안만 키보드 입력을 받고, 입력칸에서 벗어나거나 게임을 누르면 게임으로 돌려줍니다.
  `이 오버레이에 포커스를 두지 않음` 설정과 관계없이 입력칸에는 입력할 수 있습니다.
* 오버레이 안에 저장된 데이터(localStorage 등)는 처음 WebView2로 바꿀 때 CEF에서 한 번 복사해 옵니다.
  이후로는 두 엔진이 따로 저장하므로, WebView2에서 바꾼 오버레이 안 설정은 CEF로 되돌리면 보이지 않습니다.
* WebView2 런타임은 Windows 10/11에 보통 설치되어 있습니다. 런타임을 쓸 수 없으면 CEF로 그리고 그 이유가 `렌더러` 아래에 나옵니다.
  [Microsoft WebView2 런타임](https://developer.microsoft.com/microsoft-edge/webview2/)을 설치하고 ACT를 재시작하면 WebView2로 그립니다.
* 엔진을 오버레이마다 섞어 써도 됩니다. 엔진마다 그 엔진을 쓰는 오버레이가 하나라도 있으면 공통 프로세스가 한 번 뜹니다
  (메모리 CEF 약 260MB, WebView2 약 150MB). 모든 오버레이를 한 엔진으로 맞추면 다른 엔진은 켜지지 않습니다.
  오버레이마다 드는 프로세스(약 80~100MB)는 어느 엔진이든 비슷합니다.

</details>

<details>
<summary>CEF로 그릴 때: GPU와 프레임 레이트</summary>

* `일반`(`General`) 탭의 `GPU로 오버레이 그리기`(기본 켜짐)는 오버레이를 GPU에서 그린 그대로 표시해, 프레임 레이트를 높여도 CPU를 적게 씁니다.
  오버레이가 깨지거나 표시되지 않으면 끄고 ACT를 재시작합니다. WebView2로 그리는 오버레이는 항상 GPU로 그립니다.
* 프레임 레이트는 오버레이마다 `고급` 탭의 `최대 프레임 레이트 (FPS)`에서 정합니다(새 오버레이는 60).
  `모니터에 맞춤`을 체크하면 모니터 주사율의 1.5배로 그립니다. CEF는 모니터 갱신에 맞춰 그리지 않아서,
  주사율과 같은 값이면 가끔 같은 장면이 한 번 더 보이며 끊겨 보입니다. 여유 있게 그리면 거의 매 갱신마다 새 장면이 들어옵니다.

</details>

## 지켜야 할 것

* ACT의 `Get Plugins`나 해루봇으로 OverlayPlugin을 설치·업데이트하지 않습니다. 본가로 덮어씌워집니다.
  해루봇에 업데이트 버튼이 보여도 누르지 않습니다.
* 업데이트는 ACT를 실행할 때 자동으로 확인하고, 새 버전이 있으면 설치할지 묻습니다.
  `일반` 탭의 `업데이트 확인` 버튼으로 직접 확인할 수도 있습니다.

<details>
<summary>해루봇의 OverlayPlugin 관련 동작</summary>

해루봇으로 ACT를 실행해도 문제없습니다.

* 다운로드·업데이트 버튼: `Plugins\OverlayPlugin` 폴더를 지우고 본가를 새로 받아 넣습니다. 본가에 새 릴리즈가 나오면 업데이트 버튼이 표시됩니다.
* 삭제 버튼: `Plugins\OverlayPlugin` 폴더를 지우고 ACT 플러그인 목록에서 제거합니다. 오버레이 목록과 설정 파일은 지우지 않습니다.
* 플러그인 연결 버튼(체인 모양): ACT 플러그인 목록에서 사용 여부만 바꿉니다. 파일은 그대로이므로 써도 됩니다.
* 실행될 때마다 본가의 최신 opcode 파일(`resources\opcodes.jsonc`)을 받아 덮어씁니다. 이 포크는 본가의 opcode 파일을 그대로 쓰므로 영향이 없습니다.

</details>

## 자주 묻는 것

**설정은 유지되나요?**
오버레이 목록과 설정은 OverlayPlugin 폴더가 아니라 `%APPDATA%\Advanced Combat Tracker\Config`에 저장되므로 그대로 유지됩니다.
브라우저 엔진과 오버레이 안 저장 데이터는 본가와 다른 폴더(`%APPDATA%\Advanced Combat Tracker`의 `OverlayPluginCef-cbkr`, `OverlayPluginCache-cbkr`, `OverlayPluginWebView2-cbkr`)를 씁니다.
처음 실행할 때 본가의 오버레이 저장 데이터를 한 번 복사해 오고, 본가 폴더는 그대로 남습니다.

**제대로 설치됐는지 확인하려면?**
`OverlayPlugin.dll` 파일을 마우스 오른쪽 버튼으로 클릭 → **속성** → **자세히**에서 파일 설명이 `OverlayPlugin (커스텀)`이고 파일 버전이 네 자리(`0.19.110.2` 형식)이면 이 포크입니다. 버전은 `본가 버전.포크 번호`입니다.

**오류가 나거나 오버레이가 비어 있어요.**
zip의 차단 해제를 하지 않고 압축을 풀었을 수 있습니다. ACT를 종료하고 차단 해제한 zip으로 다시 넣습니다.

**오버레이가 깨지거나 일부만 보여요.**
WebView2로 그리는 오버레이라면 그 오버레이의 `WebView2로 그리기`를 끕니다. CEF라면 `GPU로 오버레이 그리기`를 끄고 ACT를 재시작합니다.

**업데이트 알림이 안 떠요.**
위의 방법으로 버전이 네 자리인지 확인합니다.

**본가 OverlayPlugin으로 되돌리려면?**
ACT를 종료하고 `Plugins\OverlayPlugin` 폴더를 지운 뒤, 설치할 때 옮겨 둔 기존 폴더를 다시 넣습니다.
옮겨 둔 폴더가 없으면 ACT의 `Get Plugins`나 해루봇으로 OverlayPlugin을 설치합니다. 오버레이와 설정은 그대로 유지됩니다.

**그 밖의 문제는?**
[본가 FAQ](https://overlayplugin.github.io/docs/faq/)를 참고하거나 [디스코드](https://discord.gg/R75tq68bA9)로 문의합니다.

## 빌드

플러그인을 수정하려는 경우에만 해당합니다. Visual Studio와 .NET 데스크톱 개발 워크로드를 설치하고, `tools/fetch_deps.ps1`을 실행한 뒤 `build.bat`을 실행하면 `out\Release` 또는 `out\Debug`에 `OverlayPlugin.dll`이 생성됩니다.

본가 문서: [README (영어)](https://github.com/OverlayPlugin/OverlayPlugin/blob/main/README.md), [오버레이 만들기](https://overlayplugin.github.io/OverlayPlugin/devs/)

## 라이선스

MIT 라이선스. 자세한 내용은 LICENSE.txt를 참고하세요.
