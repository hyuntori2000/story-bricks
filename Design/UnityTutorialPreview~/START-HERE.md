# Story Bricks — Tutorial Preview

디바이스 없이 재생할 수 있는 독립 Unity 프로젝트입니다. 원본 뷰어 UI의 그림·음성을 사용합니다.

## 실행

1. Unity Hub에서 **이 폴더**를 프로젝트로 추가합니다. 확인한 버전은 **Unity 6000.3.28f1**입니다.
2. `Assets/StoryBricks/Scenes/TutorialPreview.unity`를 엽니다.
3. ▶ Play를 누르면 **오프닝 → FOX 튜토리얼 자동 재생 → 메뉴** 순으로 진행합니다.
4. Game 화면을 클릭한 뒤 키보드로 조작합니다.

| 입력 | 동작 |
| --- | --- |
| **P** | 더블 핀치 1회를 가상 입력. 튜토리얼 중에는 전체 튜토리얼을 스킵하고 메뉴로 돌아갑니다. |
| **T** | 메뉴에서 튜토리얼 다시 재생 |
| **Space / →** | 튜토리얼 밖에서 기존 이야기 데모의 다음 단계 |
| 마우스 | 메뉴 버튼 선택 |

튜토리얼은 FOX 만들기, 두 번 핀치하는 손동작, 마침표 블록 설명, 여우 등장까지 자동으로 진행됩니다. 진행 중 더블 핀치는 단계를 넘기는 대신 튜토리얼 전체를 종료합니다. 실제 Quest용 `ParentUI.unity`에도 같은 흐름이 적용되지만, 실제 손 추적은 기기에서 별도 확인해야 합니다.

## 파일과 설정

- `TutorialPreview.unity`: XR 장비가 필요 없는 별도 씬.
- `ParentUI.unity`: 원본 Quest 씬. 기존 씬 파일을 보존하며 공유 `ViewerFlow` 동작만 개선했습니다.
- `ViewerFlow.cs`: `Tutorial After Opening`으로 자동 시작을, `Auto Play Tutorial`로 자동 진행을 설정합니다. `SkipTutorial()` 및 `HandleDoublePinch()`를 외부 입력에 연결할 수 있습니다.
- `TutorialPreviewBuilder.cs`: **StoryBricks → Create Desktop Tutorial Preview**로 미리보기 씬을 다시 생성합니다.
- `TutorialPlaybackVerification.cs`: Unity Play Mode 자동 검증 진입점.

현재 GitHub 루트의 인식·백엔드 제품에 연결하거나 배포한 상태는 아닙니다. 별도 프로젝트이므로 Unity Hub에는 저장소 루트가 아닌 이 폴더를 추가하세요. 원본 아카이브는 Unity 6000.0.85f2였으며 이 사본은 설치된 6000.3.28f1로 가져와 검증했습니다.

## 검증

Unity Editor Play Mode에서 오프닝 이후 자동 시작, 네 구간의 스킵, 스킵 후 정리, 정상 완료·재재생, 이후 이야기 시작을 확인합니다. 반복 검사는 실행 중에만 일부 시간을 줄이고 음성을 생략하며 씬에는 저장하지 않습니다. 실제 손 추적 정확도는 이 검사 범위에 포함되지 않습니다.

```sh
Unity -batchmode -projectPath /path/to/this-project \
  -executeMethod TutorialPlaybackVerification.Run -logFile tutorial-check.log
```

`-quit`은 붙이지 않습니다. 검사가 완료되면 종료되며 프로젝트 상위 폴더에 `tutorial-verification.json`을 기록합니다.

## 테스트 영상

[실제 Unity 재생 녹화](Verification/tutorial-test.mp4): 1280×800, 약 73초, 원래 음성 포함. 오프닝과 전체 튜토리얼이 자동 진행된 뒤, 재재생 중 가상 더블 핀치로 스킵하고 메뉴에 돌아오는 동작을 보여줍니다. 영상·음성은 Git LFS로 저장되므로 복제 후 `git lfs pull`을 실행하세요.

다시 녹화하려면 Play를 중지하고 **StoryBricks → Record Tutorial Test**를 선택합니다. 녹화 도구가 전체 재생과 스킵을 순서대로 실행합니다. 원본 프레임과 WAV는 `work/tutorial-recording`에 저장됩니다. 이 도구는 에디터 전용이며 제품 빌드에는 포함되지 않습니다.
