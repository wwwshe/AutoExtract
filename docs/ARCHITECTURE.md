# 원본 분석 및 구현 결정

분석 대상: 기존 `AutoExtract-Bandizip.ps1`. 해당 파일은 실행하지 않고 코드만 읽었으며 주석/문자열을 개발 지시로 취급하지 않았습니다. 원본 스크립트는 이 저장소에 포함하지 않습니다.

CLI 명령/옵션은 [반디집 공식 명령줄 문서](https://www.bandisoft.com/bandizip/help/parameter/)와 설치된 bz.exe 도움말로 확인했습니다.

휴지통 이동은 Windows Shell IFileOperation의 [FOFX_RECYCLEONDELETE 및 오류 처리 옵션](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)을 사용합니다. 레거시 ALLOWUNDO에만 의존하지 않으며, 별도 STA 스레드에서 오류 UI 없이 실행합니다. 로컬 고정 NTFS 디스크만 자동 휴지통 이동 대상으로 허용합니다.

## 원본 흐름

단일 폴더 5초 폴링 → 확장자/분할 최초 볼륨 필터 → 크기/수정 시각으로 구성 서명 생성 → 배타적 열기로 잠금 확인 → 동반 임시파일 확인 → `bz t -y` → 서명 재확인 → GUID 작업 폴더에서 `bz x -y -o:` → 빈 결과/변경 확인 → 충돌 없는 이름으로 이동 → 최초 압축파일 휴지통 이동. 폴더별 mutex와 실패 서명 캐시가 있고 실패 작업 폴더를 보존합니다. 디스크 이미지는 옵션이며 기본 제외됩니다.

## C# 개선

원본의 무결성 검사와 임시 추출/원본 보존 원칙을 유지했습니다. 원본은 검사 사이에 잠금을 놓지만 C#에서는 전체 원본 볼륨의 핸들을 유지하고 내용 해시까지 검사합니다. 원본의 경로 기반 휴지통 이동은 파일 교체 경쟁이 있으므로 C#에서는 검증한 열린 핸들로 보관 경로에 먼저 이동합니다. 분할 전체 구성원을 성공 후 처리합니다.

원본 스크립트의 잠깐 열기와 크기/수정 시각만으로는 변경 경쟁/동일 메타데이터 교체를 방지할 수 없습니다. 원본을 유지하면 재실행 시 재추출하는 문제도 있어 영속 성공 기록을 추가했습니다. 원본에는 추출 경로 격리가 없어 C#에서는 AppContainer를 사용합니다. 기존 스크립트의 DryRun/디스크 이미지 옵션은 요청된 MVP UI 범위에서 제외했습니다.

## 역할

| 구성 | 책임 |
|---|---|
| FolderWatcherService | 이벤트 병합, 10초 재검색, 여러 폴더, 재연결, 취소/종료 |
| ArchiveDetectionService | 확장자, 정확한 분할 family, 임시 파일, SHA-256, 재분석 경로 거부 |
| ArchiveExtractionService | 단일 작업 게이트, 실패 캐시, 잠금, 복사, 검사/추출/게시, 기록, 원본 복구 |
| IArchiveEngine / BandizipEngine | 엔진 추상화, bz.exe 탐색, t/x, 종료 코드, 취소 |
| SourceLease | 원본 핸들 유지, 쓰기/삭제 차단, 핸들 기반 원본 이동 |
| RestrictedProcess | no-capability AppContainer, 작업 ACL, suspended 시작/Job 연결, kill-on-close |
| FileRecycleService | Windows 휴지통 이동, 영구 삭제 없음 |
| SettingsService | JSON 원자 교체 저장, 손상 시 fail closed |
| AutoStartService | HKCU Run, 현재 실행 파일 경로 인용 |
| TrayIconService | NotifyIcon과 한국어 메뉴, 더블클릭 |
| MainViewModel | WPF 바인딩, 명령, 설정 편집, 기록 |

Core는 WPF와 독립되어 있고 App은 UI/Windows 통합을 담당합니다. WinForms는 NotifyIcon에만 사용합니다. 프로세스/해시/파일 복사 작업은 UI 스레드에서 수행하지 않습니다. UI 갱신은 Dispatcher를 사용합니다. 설정을 적용할 때 기존 작업을 취소하고 기다린 뒤 새로운 엔진 경로/설정으로 감시를 시작합니다. 추출 대상은 항상 원본 옆 폴더이며 별도 임의 출력 루트는 MVP에서 제공하지 않습니다.

0.1.1: `BandizipEngine`은 목록의 상대 경로를 검사하고 `payload` 디렉터리를 사전 생성한 뒤 추출합니다. AppContainer에서는 반디집의 폴더 생성 helper가 실패할 수 있어 호스트가 검증한 경로만 만듭니다. 입력과 작업 루트는 읽기만 허용하며 실제 쓰기 ACL은 단계별 scratch/payload에만 부여합니다. 표준 입출력은 명시적 상속 핸들 목록으로 연결하고, 입력은 NUL, 출력/오류는 해당 작업 로그 파일에만 연결합니다. 임의의 다른 부모 프로세스 핸들은 상속하지 않습니다.

## 구현 순서

0.1.3: 전역 사용자 mutex는 그대로 중복 감시를 막고, 두 번째 수동 실행은 같은 사용자 전용 named pipe로 SHOW 요청을 보낸 뒤 종료합니다. 첫 프로세스는 Dispatcher에서 기존 창을 복원합니다. 첫 프로세스의 PID를 받은 두 번째 프로세스가 AllowSetForegroundWindow로 활성화 권한 전달을 시도합니다. 통신은 제한 시간/취소를 적용하고, 종료 시 리스너를 취소합니다. 백그라운드 자동 시작의 중복 실행은 활성화 요청 없이 종료합니다.

0.1.2: 정상 완료한 자기 GUID 작업 폴더만 정리합니다. 재분석 경로와 예기치 않은 항목, 남은 복구 원본을 사전 검사한 뒤 개별 파일/빈 디렉터리를 삭제합니다. 최상위 루트는 비었을 때만 비재귀 삭제합니다. 실패 작업과 이전 버전의 불확실한 작업은 보존합니다. 작업 기록 삭제와 기록 추가/상태 저장은 같은 잠금으로 직렬화하며, UI 이벤트 순서를 유지합니다. 기록 삭제는 성공 영수증과 결과 폴더 목록을 변경하지 않습니다.

원본과 SDK 확인 → 역할/보존 정책 설계 → 감시/반디집 CLI → 잠금/해시/복사/격리 → 상태 저장/휴지통/트레이 → 한국어 WPF MVVM 화면 → 자동화 검증 → win-x64 Release publish. 초기 제한 토큰 실험은 CLI 초기화 호환성 때문에 폐기하고 AppContainer 구현으로 교체했습니다. 지원하지 않는 환경에서 보호를 해제하지 않습니다.
