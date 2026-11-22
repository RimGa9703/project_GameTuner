# GameTuner — 인수인계서

게임용 Windows 최적화 프로그램. 윈도우 설정(레지스트리, 전원 플랜, 부팅 옵션, 서비스)을 바꿔 프레임 드랍과 입력 지연을 줄이고, 바꾼 설정은 언제든 원래대로 되돌릴 수 있다.

- 스택: C# / .NET 8 / WinForms, 단일 파일 self-contained EXE (win-x64)
- 관리자 권한 필수 (`app.manifest` → `requireAdministrator`)
- 배포: GitHub Actions → Release → 앱 내 자동 업데이트

---

## 1. 현재 상태

| 항목 | 상태 |
|---|---|
| 안전 최적화 8개 | 구현 완료 |
| 공격적 최적화 10개 + 위험 경고 툴팁 + 적용 전 확인창 | 구현 완료 |
| 이미 적용된 설정 자동 체크 (사용자가 직접 바꿔 둔 설정 포함) | 구현 완료 |
| ★ 자동 최적화 (PC 환경 감지 → 추천 항목 선택 → 확인 후 적용) | 구현 완료 |
| 앱 내 업데이트 (GitHub Release 조회 → EXE 자기 교체) | 구현 완료 |
| PC 사양 보기 (CPU/GPU/RAM/메인보드/Windows/디스크) | 구현 완료 — 조회 스크립트는 실기 확인, 앱 화면은 미확인 |
| 임시 파일 정리 (사용자/Windows 임시, 업데이트 캐시, GPU 셰이더 캐시) | 구현 완료 — 크기 측정만 실기 확인, 실제 삭제·선택 창은 미확인 |
| GitHub 저장소 생성 | 완료 — `RimGa9703/project_GameTuner` |
| 첫 푸시 / Release 생성 확인 | 완료 — 공개 저장소, Actions 성공, Release `v1.0.1` (2026-11-21) |
| `Updater.Repo` 상수 | 완료 — `RimGa9703/project_GameTuner` |

### 검증 수준 (중요)
- UI가 아닌 로직(데이터, 백업, 레지스트리, 전원, bcdedit, 환경 감지, 업데이트)은 Linux에서 WinForms 스텁으로 **컴파일하고 실행까지 확인**했다. `Tweaks.All()`이 18개 항목을 반환하고, 자동 규칙의 제목이 모두 실제 항목과 일치한다.
- **`MainForm`(UI)은 실제 빌드를 한 번도 못 했다.** Windows에서 첫 빌드할 때 오류가 나면 여기부터 의심한다.
- **실제 Windows에서 적용하고 되돌리는 테스트는 한 번도 하지 않았다.** 특히 아래는 실기 확인이 필요하다:
  - `powercfg /qh` 출력에서 마지막 두 16진수를 현재 AC/DC 값으로 읽는 파싱 (코어 파킹)
  - `bcdedit /enum {current}`가 한국어 Windows에서 값을 `Yes`로 내는지 `예`로 내는지 (둘 다 처리해 두었음)
  - PowerShell `Get-PhysicalDisk` / `Get-BitLockerVolume` 조회 결과
  - 업데이트 시 실행 중인 EXE를 `.old`로 이름 바꾸고 교체한 뒤 재시작하는 흐름

---

## 2. 파일 구조

```
GameTuner/
├─ CLAUDE.md                        이 문서
├─ GameTuner.csproj                 net8.0-windows, WinForms, Version 1.0.0 (로컬 기본값)
├─ app.manifest                     관리자 권한, PerMonitorV2 DPI
├─ build.bat                        로컬에서 단일 EXE 빌드 → out\GameTuner.exe
├─ .github/workflows/release.yml    main 푸시 → 빌드 → Release v1.0.{run_number}
└─ Program.cs                       전체 코드 (단일 파일)
```

### Program.cs 구성 (위에서 아래 순서)

| 영역 | 역할 |
|---|---|
| `Program` | 진입점. `Updater.CleanupOld()` 후 `MainForm` 실행 |
| `RegVal` | 레지스트리 값 하나 (Hive, Path, Name, Value, Kind). `Key`가 백업 키 |
| `BackupEntry` | 백업 레코드 (Existed, Kind, Data) |
| `Tweak` | 최적화 항목 하나: Title, Desc, NeedsReboot, Aggressive, Risk, AutoSkip, IsApplied, Apply, Revert |
| `Updater` | GitHub Release 조회, 다운로드, 자기 교체, `.old` 정리 |
| `SysEnv` | PC 환경 감지 (노트북/충전 여부, Win10/11·빌드, SSD/HDD, BitLocker, 커널 안티치트) |
| `Specs` | PC 사양 조회 (PowerShell CIM, UTF-8 출력 → `key=값|값` 파싱). GPU 메모리는 표시 안 함 (`ponytail:` 참고) |
| `CleanTarget` / `Cleaner` | 임시 파일 정리 대상 정의, 크기 측정 `Measure`, 삭제 `Clean`. 정션은 따라가지 않고, `%TEMP%\.net`(단일 파일 런타임)은 제외. 임시 폴더는 7일 지난 파일만 |
| `Backup` | `C:\ProgramData\GameTuner\backup.json` 읽기/쓰기 |
| `Reg` | `Write`(처음 한 번만 백업 후 쓰기) / `Restore` / `Matches` / `ReadInt` |
| `Native` | `SystemParametersInfo`(마우스 즉시 반영), 외부 프로세스 실행 `Run` (encoding 생략 = 시스템 코드페이지, bcdedit/powercfg용) |
| `Power` | 고성능 전원 플랜 적용/되돌리기, `ActiveScheme()` |
| `PowerSetting` | 현재 전원 플랜의 개별 설정 (AC/DC) 변경 — 코어 파킹 |
| `Boot` | bcdedit yes/no 옵션 — 다이나믹 틱 |
| `Tweaks` | 항목 정의 `All()`, 헬퍼 `RegTweak` / `Aggressive` / `ServiceTweak` / `Auto` |
| `MainForm` | UI: ListView, 툴팁, 버튼, 로그, 자동 최적화, 업데이트 확인 |

---

## 3. 핵심 설계 규칙 (깨면 안 됨)

1. **백업은 처음 한 번만 한다.** `Reg.Write`는 백업 키가 없을 때만 원래 값을 저장한다. 다시 적용해도 튜닝한 값이 백업을 덮어쓰지 않는다.
2. **원래 값이 없던 항목도 기록한다.** `Existed = false` → 되돌릴 때 값을 삭제한다.
3. **사용자가 직접 바꿔 둔 설정은 되돌리지 않는다.** 백업이 없으면 `Restore`는 아무것도 하지 않고, UI는 `[유지]` 로그를 남긴다. 원래 값을 모르기 때문이다.
4. **이미 적용된 항목은 다시 적용하지 않는다** (`[건너뜀]`).
5. **목록 체크 상태는 실제 시스템 상태 기준이다.** `RefreshList()`는 `IsApplied()`가 true인 항목을 체크한다.
6. **"안전 항목 전체 선택"은 공격적 항목을 고르지 않는다.** 공격적 항목은 하나씩 직접 체크해야 한다.
7. **공격적 항목을 처음 적용할 때는 위험 내용을 모은 확인창을 띄운다.** 기본 선택은 "아니요"이다. 자동 최적화는 자체 요약 창에 위험 내용을 넣고 `Run(true, skipConfirm: true)`로 넘어간다.
8. **서비스가 없는 PC에서는 레지스트리 키를 만들지 않는다.** `ServiceTweak`은 빈 목록을 반환하고, `RegTweak.Apply`는 빈 목록이면 예외를 던진다. `CreateSubKey`가 가짜 서비스 키를 만드는 걸 막기 위해서다.
9. **보안을 약화시키는 항목(HVCI)은 자동 최적화가 켜지 않는다.** Defender나 Windows Update를 끄는 기능은 의도적으로 넣지 않았다.
10. **임시 파일 정리는 되돌릴 수 없다.** 백업 대상이 아니므로 선택 창에서 크기와 "되돌릴 수 없음"을 먼저 보여 주고, 셰이더 캐시는 기본 해제 상태로 둔다. 휴지통·사용자 문서 폴더는 대상에 넣지 않는다.

---

## 4. 코드 스타일 (사용자 요구사항)

- **삼항 연산자 금지** → `if/else` 또는 `switch`
- **bool 비교는 `== true` / `== false`로 명시**
- 기존 코드 컨벤션을 그대로 따른다 (섹션 구분 주석 `// ─── 이름 ───`, 한국어 로그/UI 문자열)
- 큰 수정은 **완성된 `.cs` 파일 전체**로 전달한다
- 설명은 결론부터, 짧게, 단계별로
- 코드 근거 없이 추측하지 않는다 — 확인이 안 된 건 확인이 안 됐다고 말한다
- 의도적으로 단순화한 부분에는 `// ponytail:` 주석으로 한계와 확장 방법을 적는다

---

## 5. 최적화 항목 목록

### 안전 (8)
| 제목 | 방식 | 재부팅 |
|---|---|---|
| 고성능 전원 관리 옵션 | `Power` (숨겨진 경우 복제) | X |
| 게임 모드 켜기 | HKCU GameBar | X |
| Game DVR / 백그라운드 녹화 끄기 | HKCU GameConfigStore, GameDVR | X |
| GPU 하드웨어 가속 스케줄링(HAGS) | HKLM GraphicsDrivers `HwSchMode=2` | O |
| 마우스 가속 끄기 | HKCU Mouse + `SystemParametersInfo` 즉시 반영 | X |
| Nagle 알고리즘 끄기 | 모든 Tcpip 인터페이스 `TcpAckFrequency`/`TCPNoDelay` | O |
| 멀티미디어 스케줄러 게임 우선순위 | SystemProfile + Tasks\Games | O |
| 애니메이션·투명 효과 / 백그라운드 앱 끄기 | HKCU 여러 키 | 재로그인 |

### 공격적 (10)
| 제목 | 방식 | 자동 최적화 |
|---|---|---|
| 코어 파킹 해제 | `PowerSetting` CPMINCORES=100 | 데스크톱만 |
| 전원 스로틀링 끄기 | HKLM PowerThrottling | 데스크톱만 |
| 전역 타이머 해상도 요청 허용 | HKLM kernel `GlobalTimerResolutionRequests` | Win11 데스크톱만 |
| 다이나믹 틱 끄기 | `Boot` bcdedit `disabledynamictick` | 항상 제외 |
| 전체 화면 최적화(FSO) 끄기 | HKCU GameConfigStore FSE 값 | 항상 제외 |
| 네트워크 스로틀링 해제 | `NetworkThrottlingIndex=0xffffffff` | 항상 제외 |
| 메모리 무결성(HVCI) 끄기 | HKLM DeviceGuard | 항상 제외 (보안) |
| SysMain 끄기 | `ServiceTweak` | SSD일 때만 |
| Windows Search 끄기 | `ServiceTweak` | 항상 제외 |
| DiagTrack 끄기 | `ServiceTweak` | 항상 제외 |

---

## 6. 자주 하는 작업

### 새 최적화 항목 추가
1. `Tweaks.All()`의 `list` 초기화 안에 추가한다.
   - 레지스트리 항목: `RegTweak(제목, 설명, 재부팅여부, () => new List<RegVal> { ... }, 적용후액션?, 되돌린후액션?)`
   - 공격적 항목: `Aggressive(RegTweak(...), "• 위험1\n• 위험2")`
   - 서비스: `ServiceTweak("서비스명", 제목, 설명, 위험)`
   - 전원 플랜 설정: `PowerSetting.Make(...)` / 부팅 옵션: `Boot.Make(...)`
   - 그 밖의 경우: `new Tweak { IsApplied, Apply, Revert }`를 직접 작성한다. Apply 안에서 **처음 한 번만 백업하는 규칙**을 꼭 지킨다.
2. 자동 최적화에서 조건을 걸려면 `Auto(list, "제목", e => { ... return null 또는 제외사유; })`를 추가한다. 규칙이 없으면 항상 추천된다. 제목이 틀리면 앱 시작 시 예외가 난다.
3. 값 형식은 DWord/String만 백업·복원을 지원한다. Binary/MultiString이 필요하면 `Reg.Write`/`Restore`의 직렬화를 확장한다.

### 새 환경 감지 추가
`SysEnv`에 필드를 추가하고 `Detect()`에서 채운 뒤 `ToString()`에 표시를 추가한다. PowerShell 조회는 기존 `ps` 문자열에 `'key=' + 값` 줄을 덧붙이고 파싱 루프에 분기를 추가한다. 큰따옴표는 쓰지 말고 작은따옴표만 쓴다.

### 로컬 빌드
```bat
build.bat
```
→ `out\GameTuner.exe`

### 릴리스
`main`에 푸시하면 끝이다. Actions가 `v1.0.{run_number}` Release를 만들고 `GameTuner.exe`를 올린다. 메이저/마이너 버전을 올리려면 `release.yml`의 `VERSION: 1.0.` 앞부분을 바꾼다.

---

## 7. 업데이트 구조

- `Updater.CheckAsync()` → `api.github.com/repos/{Repo}/releases/latest`를 조회하고, `tag_name`과 현재 어셈블리 버전을 비교한다(3자리로 맞춰서 비교). `GameTuner.exe` asset URL을 반환한다.
- `Updater.InstallAsync()` → `.new`로 다운로드(1MB 미만이면 거부) → 현재 EXE를 `.old`로 이름 변경 → `.new`를 원래 이름으로 → 재시작 → 종료. 교체에 실패하면 `.old`를 원래 이름으로 되돌린다.
- 앱 시작 시 `Shown` 이벤트에서 조용히 확인한다(오류는 로그만 남긴다). 버튼을 누르면 결과를 창으로 띄운다.
- **저장소는 공개여야 한다** (토큰 없이 API를 조회하기 때문).
- 해시/서명 검증은 없다 (`ponytail:` 주석 참고). 필요하면 Release에 sha256 파일을 같이 올려 비교한다.
- 로컬 빌드는 Version 1.0.0이라 Release가 하나라도 있으면 항상 업데이트 알림이 뜬다 (의도된 동작).

---

## 8. 남은 할 일 (우선순위 순)

1. [x] GitHub 공개 저장소 생성 → `Updater.Repo` 수정 → 첫 푸시 → Release 생성 확인
2. [x] Windows에서 첫 빌드 — 오류 없음 (경고 WFAC010: DPI 설정을 manifest 대신 csproj로 옮기라는 권고, 무시함)
3. [ ] 실기 테스트: 항목별 적용 → 상태 "적용됨" → 되돌리기 → 원래 값 복구 확인 (`backup.json` 내용도 확인)
4. [ ] 위 "검증 수준"에 적은 파싱 4건을 실기에서 확인
5. [ ] 업데이트 흐름 실기 테스트 (Release 2개 이상 필요)

### 아이디어 (사용자가 요청하면 진행)
- 재부팅 후 적용 결과 확인
- 프리셋 저장/불러오기
- 복제된 "최고의 성능" 플랜 인식 (현재는 원본 GUID만 인식 — `powercfg /list` 이름 파싱으로 확장)
- 업데이트 파일 해시 검증

---

## 9. 알려진 한계

- 코어 파킹은 적용할 때의 전원 플랜에만 적용된다. 목록 순서상 고성능 전원보다 아래에 있어서, 둘을 같이 적용하면 고성능 플랜에 적용된다.
- 노트북에서 고성능 플랜을 복제해 만든 경우, 되돌려도 복제한 플랜은 목록에 남는다.
- 서비스를 끄는 항목을 되돌리면 시작 유형을 복원하고 `sc start`까지 한다.
- 복원 지점 생성은 시스템 보호가 꺼져 있거나 24시간 안에 이미 만들었으면 실패한다 (Windows 기본 제한).
