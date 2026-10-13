# OverlayPlugin (한국어 개인 포크)

[![최신 릴리즈](https://img.shields.io/github/v/release/pnsdh/OverlayPlugin?color=brightgreen&sort=semver)](https://github.com/pnsdh/OverlayPlugin/releases/latest)

[OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin)을 기반으로, 오버레이 표시 성능과 한국 서버 지원을 손본 포크입니다.
[cactbot 한국어 포크](https://github.com/pnsdh/cactbot)와 함께 쓰는 것을 기준으로 합니다.

문의·버그 제보는 [디스코드](https://discord.gg/R75tq68bA9)로 해 주세요.

* 본가 OverlayPlugin의 기능을 모두 포함하며, 본가 업데이트는 자동으로 반영되어 함께 릴리즈됩니다.
* 오버레이 최대 프레임 레이트를 240까지 설정할 수 있습니다. (본가는 60까지)
* 브라우저 엔진(CEF)을 152로 올렸습니다. (본가는 95)
* GPU로 오버레이를 그리는 옵션이 있습니다. ([GPU로 그리기](#gpu로-그리기))
* 한국 서버에서 초읽기 로그 줄이 깨지던 문제를 고쳤습니다.
* 기존 OverlayPlugin의 오버레이와 설정은 그대로 이어서 사용합니다. ([설정 유지](#설정-유지))
* 버전은 `본가 버전.포크 번호` 형식입니다. 예를 들어 `0.19.110.1`은 본가 0.19.110 기반의 첫 번째 포크 릴리즈입니다.

## 목차

1. [설치](#설치)
1. [GPU로 그리기](#gpu로-그리기)
1. [해루봇 사용 시 참고사항](#해루봇-사용-시-참고사항)
1. [설정 유지](#설정-유지)
1. [업데이트](#업데이트)
1. [설치 확인](#설치-확인)
1. [문제 해결](#문제-해결)
1. [본가 OverlayPlugin으로 되돌리기](#본가-overlayplugin으로-되돌리기)

## 설치

ACT와 FFXIV ACT Plugin, 본가 OverlayPlugin이 설치되어 있는 상태에서 OverlayPlugin 폴더만 바꿉니다.

1. ACT의 `Plugins` 탭 → `Plugin Listing`에서 `OverlayPlugin.dll`의 경로를 확인합니다.
   보통 `...\Advanced Combat Tracker\Plugins\OverlayPlugin\OverlayPlugin.dll`입니다.
1. ACT를 종료합니다.
1. [최신 릴리즈](https://github.com/pnsdh/OverlayPlugin/releases/latest)에서 `OverlayPlugin-버전.zip`을 내려받습니다.
1. zip 파일을 마우스 오른쪽 버튼으로 클릭 → **속성**에서, 아래쪽에 **차단 해제** 체크박스가 있으면 체크하고 **확인**을 누릅니다.
1. 기존 `OverlayPlugin` 폴더를 ACT 폴더 밖으로 옮겨 둡니다. 되돌릴 때 사용합니다.
1. 압축을 풀어 나온 `OverlayPlugin` 폴더를 같은 자리에 넣습니다.
   ACT 폴더는 보통 `C:\Program Files (x86)\Advanced Combat Tracker`이며, 관리자 권한 확인 창이 뜨면 **계속**을 누릅니다.
1. ACT를 실행합니다. 처음 실행할 때 새 브라우저 엔진(CEF)을 자동으로 내려받으므로 진행 창이 닫힐 때까지 기다립니다.

경로가 그대로이므로 ACT에 플러그인을 다시 추가할 필요는 없습니다.

설치 후에는 ACT의 `Get Plugins`나 해루봇으로 OverlayPlugin을 설치·업데이트하지 않습니다. 본가 OverlayPlugin으로 덮어씌워집니다.
이 포크의 업데이트는 [업데이트](#업데이트)에 설명된 방식으로 진행됩니다. ([해루봇 사용 시 참고사항](#해루봇-사용-시-참고사항))

## GPU로 그리기

ACT의 `Plugins` 탭 → `OverlayPlugin.dll` 탭 → `일반`(`General`)에서 `GPU로 오버레이 그리기`를 체크하고 ACT를 재시작하면 적용됩니다.

* 오버레이를 GPU에서 그린 그대로 화면에 표시해, 프레임 레이트를 높여도 CPU 사용량이 적습니다.
* 기본값은 꺼짐입니다. 오버레이가 깨지거나 표시되지 않으면 체크를 끄고 ACT를 재시작합니다.
* 프레임 레이트는 오버레이마다 `일반`(`General`) 탭의 `최대 프레임 레이트 (FPS)`에서 설정합니다.

## 해루봇 사용 시 참고사항

해루봇으로 ACT를 실행해도 문제없습니다. 해루봇의 OverlayPlugin 관련 동작은 다음과 같습니다.

* 다운로드·업데이트 버튼: `Plugins\OverlayPlugin` 폴더를 지우고 본가 OverlayPlugin을 새로 받아 넣습니다.
  본가에 새 릴리즈가 나오면 업데이트 버튼이 표시되지만, 누르면 이 포크가 본가로 바뀌므로 누르지 않습니다.
* 삭제 버튼: `Plugins\OverlayPlugin` 폴더를 지우고 ACT 플러그인 목록에서 OverlayPlugin 항목을 제거합니다.
  오버레이 목록과 설정 파일은 지우지 않습니다.
* 플러그인 연결 버튼(체인 모양): ACT 플러그인 목록에서 사용 여부만 바꿉니다. 파일은 그대로이므로 사용해도 됩니다.
* 해루봇은 실행될 때마다 본가의 최신 opcode 파일(`resources\opcodes.jsonc`)을 받아 덮어씁니다.
  이 포크는 본가의 opcode 파일을 그대로 쓰므로 영향이 없습니다.

## 설정 유지

오버레이 목록과 설정은 OverlayPlugin 폴더가 아니라 ACT의 설정 폴더(`%APPDATA%\Advanced Combat Tracker\Config`)에 저장됩니다.
따라서 폴더를 바꿔도 오버레이와 설정은 그대로 유지됩니다.

브라우저 엔진과 오버레이 저장 데이터는 본가와 다른 폴더를 사용합니다.

* 브라우저 엔진: `%APPDATA%\Advanced Combat Tracker\OverlayPluginCef-cbkr`
* 오버레이 저장 데이터: `%APPDATA%\Advanced Combat Tracker\OverlayPluginCache-cbkr`

처음 실행할 때 본가의 오버레이 저장 데이터를 한 번 복사해 오므로, 오버레이 안에서 저장한 설정도 이어집니다.
본가 폴더는 그대로 남아 있어 본가로 되돌려도 영향이 없습니다.

## 업데이트

`자동으로 업데이트 확인하기`가 체크되어 있으면 ACT를 실행할 때 이 포크의 새 릴리즈를 확인하고, 새 버전이 있으면 업데이트 여부를 묻습니다.
ACT의 `Plugins` 탭 → `OverlayPlugin.dll` 탭 → `일반`(`General`)의 `업데이트 확인` 버튼으로 직접 확인할 수도 있습니다.

## 설치 확인

`OverlayPlugin.dll` 파일을 마우스 오른쪽 버튼으로 클릭 → **속성** → **자세히**에서 파일 버전을 확인합니다.
버전이 네 자리(`0.19.110.1` 형식)이면 이 포크입니다.

## 문제 해결

* ACT 실행 시 OverlayPlugin 관련 오류가 나거나 오버레이가 비어 있는 경우:
  zip의 차단 해제를 하지 않고 압축을 풀었을 수 있습니다. ACT를 종료하고, 차단 해제한 zip으로 다시 넣습니다.
* 오버레이가 깨지거나 일부만 표시되는 경우: [GPU로 그리기](#gpu로-그리기)를 끄고 ACT를 재시작합니다.
* 업데이트 알림이 뜨지 않는 경우: [설치 확인](#설치-확인)에서 버전이 네 자리인지 확인합니다.
* 그 밖의 문제는 [본가 FAQ](https://overlayplugin.github.io/docs/faq/)를 참고하거나 [디스코드](https://discord.gg/R75tq68bA9)로 문의합니다.

## 본가 OverlayPlugin으로 되돌리기

1. ACT를 종료합니다.
1. `Plugins\OverlayPlugin` 폴더를 삭제하고, 설치할 때 옮겨 둔 기존 `OverlayPlugin` 폴더를 다시 넣습니다.
   옮겨 둔 폴더가 없으면 ACT의 `Get Plugins`나 해루봇의 업데이트·다운로드 버튼으로 OverlayPlugin을 설치합니다.
1. ACT를 실행합니다. 오버레이와 설정은 그대로 유지됩니다.

## 빌드

플러그인을 수정하려는 경우에만 해당합니다.

* Visual Studio와 .NET 데스크톱 개발 워크로드를 설치합니다.
* `tools/fetch_deps.ps1`을 실행한 뒤 `build.bat`을 실행합니다.
* `out` 폴더의 `Release` 또는 `Debug` 하위 폴더에 `OverlayPlugin.dll`이 생성됩니다.

본가 문서: [README (영어)](https://github.com/OverlayPlugin/OverlayPlugin/blob/main/README.md),
[오버레이 만들기](https://overlayplugin.github.io/OverlayPlugin/devs/)

## 라이선스

MIT 라이선스. 자세한 내용은 LICENSE.txt를 참고하세요.
