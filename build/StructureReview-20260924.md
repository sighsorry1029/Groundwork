# Groundwork 구조 검토 및 적용 기록 — 2026-09-24

## 기준과 범위

- 시작 상태: `main`, `fc27bcc5ff40df2e75bf7215a857df1864fc15fe`, 미커밋 변경 없음. 이미 `origin/main`보다 1커밋 앞서 있었으며, 기존 terrain 설명 줄바꿈 변경은 이번 작업에 포함하지 않았다.
- 전역 `C:\Users\blizz\.codex\AGENTS.md`와 Valheim `INDEX.md`, 현재 README, 빌드 파일, `build/HarvestSkillVerification.md`, 관련 Git 이력을 대조했다. 작업 경로와 상위 경로에서 별도 프로젝트 AGENTS.md는 발견하지 못했다.
- `Groundwork.sln`은 .NET Framework 4.8 / AnyCPU 라이브러리 하나를 빌드한다. 명시된 Compile 항목 27개 중 vendor `LocalizationManager/LocalizationManager.cs`를 제외한 자체 코드의 책임·호출·수명주기 경계를 검토했다. 파일별 모든 가능한 입력을 실행 검증했다는 의미는 아니다.
- 진입점은 `GroundworkPlugin : BaseUnityPlugin`의 Unity `Awake/OnDestroy`와 Harmony `PatchAll`이다. 일반 BepInEx 플러그인이며 프리로더 패처가 아니다. 공통 대상의 순서는 `GroundworkHarmonyDispatch.cs`, 기능별 패치는 각 시스템 파일에 있다.
- CFG의 키·기본값·동기화 여부, terrain `Groundwork.yml`, growth의 `plants.yml/pickables.yml/cultivation.yml` 입력·참고 출력, 기본 terrain YAML 및 영어·한국어 번역의 로딩/포함/사용 경계를 확인했다. 모든 번역의 문장 품질이나 모든 prefab 조합은 검토 범위 밖이다.
- 선택적 연동은 Jewelcrafting, ZenBeehive, Expand World Data이다. PlantEverything은 명시적 incompatibility이며 Jotunn 의존성은 없다. YouAreNotWorthy는 상점 조건 설명에서 안내하며, 실제 key 판정을 가로채는 새 연동 API는 없다.
- `environment.props`의 설치 게임 원본 DLL 참조 → `Groundwork.dll` → `ILRepack.targets`에서 ServerSync 및 YamlDotNet internalize 병합 → `GetAssemblyVersion` → `CopyOutputDLL` 순서다. Debug에서는 Release ZIP target이 실행되지 않는다.
- ServerSync는 `Libs/ServerSync.dll` 고정 참조이며 SHA-256 `B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846`가 전역 `valheim-1.0.7-r1` manifest와 일치한다. 전역 INDEX의 과거 Groundwork 빌드 중 IL 수정 설명은 현재 코드와 달라 채택하지 않았다. 현재 병합 입력을 직접 확인했다. YamlDotNet 16.3.0, ILRepack task 2.0.44.1을 유지했다.
- 테스트는 `build/VerifyCompatibility.ps1` 및 `TestCompatibility.ps1`/`CompatibilityProbe.cs`/`HarvestSkillProbe.cs`/`CompatibilityMonoHost.cs`이다. 일반 Unity 테스트 프로젝트는 없다.

제외: bin/obj/배포 ZIP/IDE 생성물의 구조 개편, vendor 라이브러리 내부 전체 감사, 게임 전체와 다른 모드 전체의 분석, 메시·아이콘의 시각적 품질, 다른 플랫폼 실행. 기존 원본·추출 자료를 재사용했고 게임 DLL publicize나 신규 전체 추출을 하지 않았다. 에이전트는 읽기 전용 조사·diff 검토만 했고 편집·빌드·커밋은 한 세션에서 수행했다.

## 게임 버전과 원본 참조

기존 변경 `351422d`는 1.0.12 대응을 기록하고, 수확 소유권 검증 문서는 1.0.14 원본을 기준으로 한다. 현재 실제 컴파일 참조는 설치된 Windows x64 클라이언트 **1.0.15 / build 25390630**이다. 설치 `assembly_valheim.dll` SHA-256 `59F53FB55D99D22A33E8ED094EEC8D21E9F133543BCE92BC3D80DCE44033ADB1`는 보관된 해당 버전 원본과 일치한다. 프로젝트에는 게임 버전별 다중 빌드 대상이 없다. 이 사실과 자동 검사 통과를 새 게임 버전 지원 정책이나 실제 플레이 검증으로 확대 해석하지 않는다.

사용한 원본은 전역 `references/valheim/snapshots` 아래 다음 자료이다.

| 역할 | 버전 / Steam build | 스냅샷 디렉터리 |
| --- | --- | --- |
| 클라이언트 | 1.0.15 / 25390630 | `client-b25390630-windows-x64-20260918T131715Z` |
| 데디케이트 | 1.0.15 / 25390671 | `dedicated-server-b25390671-windows-x64-20260918T185703Z-depot-restored` |
| 클라이언트 | 1.0.14 / 25364265 | `client-b25364265-windows-x64-20260917T122143Z` |
| 데디케이트 | 1.0.14 / 25364309 | `dedicated-server-b25364309-windows-x64-20260917T122143Z-depot-restored` |

기존 GameAccess는 private 필드/메서드를 캐시한 Harmony 접근자에 모은다. 원본 `Beehive.m_nview`, `Pickable.m_pickedTime`의 private 제한과 현재 공개 설정 필드의 차이를 유지했다. 이번 변경은 새로운 비공개 게임 접근을 추가하지 않는다. 원본 `Localization.instance`는 lazy getter이며 Unity Resources 초기화를 요구하므로, null fallback만 기대하고 격리 호스트에서 hover를 실행할 수 있다고 가정하지 않았다.

## 영역별 구조 판정

전체적으로는 균형이 잡혀 있지만 terrain·growth·beehive에 책임 집중이 있다. 집중도가 높은 파일을 일괄 분리할 근거는 부족하다. 작은 상태 저장소·도우미 수준의 불필요한 분산을 먼저 줄였다.

| 영역 | 판정과 현재 배치 유지 이유 | 실제 변경 사례 |
| --- | --- | --- |
| Plugin / 설정 / ConfigLoader / 공통 Harmony dispatch | 적절한 경계. 초기화·공유 이벤트 순서·설정 등록을 찾기 쉽다. Terrain YAML의 블록별 오류 허용과 growth의 전체 정규화 정책은 다르므로 watcher/parser를 통합하지 않았다. | `a9898d3` hover enum 변경은 설정과 표시 소비자에 전파됨 |
| TerrainToolRangeSystem / MassPlantingSystem | 집중된 편. 프리뷰·배치 후보·클릭 snapshot·비용 정산이 함께 변한다. 새 manager나 파일 분리보다 동일 클릭 상태의 소유권을 정리했다. | `edf9ac8` grid preview 일치, `a65cefb` 중단된 대량 심기의 비용 정산 |
| Pickaxe / TerrainOperationSync / 부유 텍스트 | 적절한 분리. 곡괭이의 임시 설정·복원과 RPC 확장 형식, 짧은 HUD 객체 수명은 서로 다른 경계다. Hoe와 pickaxe의 복원 필드도 다르다. | 앞선 호환성·글꼴 수정이 해당 경계에 국소화됨 |
| GrowthOverrideSystem / CultivationSystem | Growth는 YAML·참고 출력·EWD 책임이 집중되어 있으나 정규화/재적용 상태를 공유한다. Cultivation은 등록·복원·기존 설치물 보존을 함께 두는 편이 적절하다. | `edd9464` 자연산 제거가 주로 Cultivation에 집중 |
| FarmingSkillSystem | Plant와 Pickable 진행 처리의 유사함은 과도한 분리의 근거가 아니다. 단일 식물 수명/checkpoint와 pickedTime 기반 반복 cycle/reset 정책을 유지했다. 공통 적분은 이미 `ProjectDynamicBonusWork`에 있다. | `9f21776`에서 원격 스킬 관측과 실제 행위자 스킬을 구분 |
| Beehive / EnvironmentEffect | 생산·공간 할당·hover/preview가 집중된 편. 실제 수분 할당 캐시와 UI가 같은 결과를 사용한다. 현재는 작은 텍스트 일반화만 제거했다. 환경 배율 공유는 유지한다. | `b36b302`, `a9898d3`의 hover 색·표시 수준 변경 |
| HarvestSkillSync / ZenBeehiveCompat | 적절한 분리. 전자는 RPC 행위자·revision·receipt 수명이고 후자는 container ownership handshake와 샘플 감소량이다. 정책을 합치지 않았다. | `9f21776`의 네트워크 요청/응답 공동 배치 |
| PickedVisual / hover proxy / Farming tooltip / KeyHintCell | 객체·UI 수명 경계가 적절하다. 숨겨진 Pickable proxy는 표시 Off에서도 수분 발견에 필요하다. 간접 호출과 외부 UI 변경 대응을 이유 없이 제거하지 않았다. | `8054b9e`는 tooltip lifecycle 일체를 dispatch에서 이동 |
| Scythe 수확 / 분류·상점 연동 | 범위 수확과 아이템 분류·Jewelcrafting·상점 key는 변경 이유가 다르다. 작은 ScytheHandleUnlockSystem은 기존 파일에 공동 배치하는 현재 구조가 적절하다. | 상점 key 변경은 실제 key 판정 구현을 대체하지 않음 |

## 구현한 개선과 안전한 적용 순서

각 단계는 수정 → Debug 빌드/해당 검사 → diff 및 읽기 전용 동료 검토 → 개별 커밋 순서로 완료했다. 세 코드 커밋은 각각 독립적으로 되돌릴 수 있다. 새 런타임 파일·인터페이스·전역 캐시는 추가하지 않았다.

### 1. `ec8803d` — 지형 클릭 상태 공동 배치

- 문제/호출: `TerrainToolRangeSystem.BeginTryPlacePiece → PrepareTerrainOp → ApplyCapturedGridPreviewPosition → EndTryPlacePiece`에서 `ActivePlacements`와 `ActiveGridPlacementStates`가 같은 Player와 클릭 수명을 병렬로 관리했다. 종료·설정 재적용 때 두 사전을 함께 지워야 했다.
- 최소 변경: 기존 `ActivePlacementContext`에 nullable `GridPlacementState`를 넣고 별도 Dictionary를 삭제했다. PrepareTerrainOp가 이미 얻은 context의 state를 전달한다.
- 효과/비용: 중복 add/remove/clear와 추가 조회를 없앴다. 기존 컨텍스트 필드 하나가 늘지만 새 호출 계층·파일 이동·캐시는 없다.
- 보존/위험: immutable preview 복제, 클릭별 used 배열, 경로 일치/마지막 하나의 fallback, 성공 비용 기록, Finalizer의 실패 정리를 유지했다. context 게시가 state 생성 뒤로 이동하지만 생성 중 이벤트·RPC·Unity 객체 생성·외부 callback은 없다. 생성 예외 시 두 구현 모두 비용을 기록하지 않고 종료한다.
- 검증: snapshot 불변성, 두 클릭의 독립 소비, 경로 일치, 복수 후보 fallback 거부, 최종 하나 fallback, 소비 완료 후 재사용 거부의 7개 관리 코드 검사를 추가했다. 동일 검사는 변경 전 DLL에서도 통과했다. 실제 Unity Begin/End 호출·비용 정산까지 실행한 검사는 아니다.

### 2. `609a3a2` — 활성 재배 규칙 조회 단일화

- 문제/호출: `GrowthOverrideSystem`의 placement biome 검사와 `MassPlantingSystem`의 후보 검사에서 `TryGetPlanting`, `HasPlacementBiomeOverride`, `IsPlacementBiomeAllowed`가 같은 등록/plantable 조건을 반복했다. 한 IsPlacementBiomeAllowed 호출에서 prefab 이름을 최대 네 번 추출하고 규칙을 다시 indexer로 읽었다.
- 최소 변경: 같은 파일의 private `GetPlantingRule(Piece)`가 이름을 한 번 구하고 현재 등록 및 규칙을 조회한다. 기존 internal 호출 시그니처를 보존했다.
- 효과/비용: 동일 정책의 수정 지점과 중복 검색을 줄였다. 작은 private helper 한 단계가 생기지만 중첩된 기존 도우미 호출과 재조회를 대체하며 파일 이동이나 캐시 무효화 비용은 없다. 프레임 시간 개선은 미측정이다.
- 보존/위험: 활성 규칙 조회 및 TryGetPlanting/HasPlacementBiomeOverride는 null/파괴된 Piece, 등록 실패, plantable false/null을 제외한다. IsPlacementBiomeAllowed 자체는 이전처럼 유효한 Piece를 전제로 먼저 m_onlyInBiome를 읽는다. Biomes 생략이면 원래 mask를 사용하고 명시한 biome 해석 실패는 계속 false이다. 매 호출 현재 Rules/Registrations를 읽어 live 변경을 유지한다.
- 검증: 기존 분기와 결과를 소스 대조하고 원본 DLL 검사 및 기존 Mono suite를 실행했다. Unity 객체를 흉내 내며 구현을 반복하는 새 테스트는 추가하지 않았다. 일반/대량 심기, 레시피 enable/disable, EWD biome 및 live YAML 변경은 실제 게임에서 확인해야 한다.

### 3. `67b1e42` — 벌집 밤·비 hover 조합 단순화

- 문제/호출: `BeehivePollinationSystem.AppendHoverText → AppendCurrentHoneyRateLine`은 호출자가 하나인데 고정 번역 key/fallback 네 개를 인자로 넘기고 최대 두 조각을 위해 List를 생성했다.
- 최소 변경: 고정 key와 표시 정책을 처리 메서드에 두고 0~2개 문자열을 직접 결합한다. private 시그니처만 줄였다.
- 효과/비용: 표시 정책 탐색 위치와 일회용 컬렉션 할당을 줄였다. 새로운 파일·추상화·캐시가 없다. 전체 성능 이득은 계측하지 않았다.
- 보존/위험: 양쪽 `< 0.999f`, 밤→비 순서, 두 공백, 색상, 줄 추가, 빈/null 기존 hover의 처리와 번역 키·fallback을 유지한다. 항목이 없으면 기존 문자열을 그대로 보존한다.
- 검증: 0/1/2조각과 임계/NaN 비교의 기존 분기를 대조하고 빌드·정적·기존 Mono suite를 통과했다. 실제 localized hover를 격리 호스트에서 실행하지는 않았다. 단순 텍스트 변경을 위해 Unity Localization 내부를 대체하는 fixture를 늘리지 않았다.

## 보존한 계약과 이번에 유지한 부분

- Harmony 대상/오버로드, 우선순위와 HarmonyAfter, 반환값, `__state`, `__runOriginal`, Finalizer 예외 전달을 변경하지 않았다. 임시 terrain/biome/description 및 honey rate/capacity의 복원도 유지한다.
- Unity Awake/Update/LateUpdate/OnDestroy, Watcher 및 CustomSyncedValue 구독 해제, UI Mesh/Material/TMP 자산 해제, shader 미지원 데디케이트 분기를 유지한다. 반복되는 Finalizer/Shutdown 보호는 역할과 경로가 달라 삭제하지 않았다.
- CFG key/default/sync 여부, enum 이름/값, YAML 및 ZDO 저장 형식, RPC 식별자, ModGUID/버전, 공개 API, embedded 리소스를 변경하지 않았다. DTO public setter, ConfigurationManager Order, Unity 메시지는 직접 참조가 없더라도 간접 호출 계약이다.
- 로컬 Player의 입력/HUD와 서버/owner의 세계 상태 갱신을 구분한다. 전용 서버가 캐릭터의 실제 Farming 저장 상태를 소유한다고 가정하지 않았다.
- HarvestSkillSync의 sender↔actor owner 검증, target revision 예약, 중첩 request context의 Finalizer 복원, receipt 일회 소비 후 XP 지급을 유지한다. owner와 사용자 권한은 동일 개념이 아니다. ward/no-build/거리 및 게임 원래 수확/제거 검증은 그대로다.
- 새 파일 분리, 성장 두 정책 통합, 환경 클래스의 벌집 pause 메서드 이동, 낮은 실익의 wrapper 삭제는 보류했다. 사용처 없는 듯한 필드의 삭제도 이번 세 개선과 무관하게 묶지 않았다.

## 프레임 경로와 별도 조사 후보

프리뷰는 기존 Line/Mesh/ghost와 NonAlloc 물리 버퍼를 재사용한다. Terrain은 갱신 간격/signature를, mass planting은 0.2초 비용 cache와 실제 클릭 시 강제 갱신을 사용한다. Farming tooltip은 root/text 변경에 맞춰 생성/레이아웃을 처리하고 LateUpdate에는 위치를 맞춘다. GameAccess의 reflection 검색은 캐시되어 있다. KeyHintCell의 자식 탐색은 외부 UI 변경 대응이므로 개수만 보고 생략하지 않았다.

벌집 preview는 대상 상태/mesh 약 0.25초, terrain range geometry 0.5초, 수분/할당 cache 3초 갱신과 30초 prune을 사용한다. 파괴된 대상/idle 상태를 정리한다. Hover 대상의 component 탐색과 문자열 생성은 남아 있고, 2048 collider 상한은 성능만의 문제가 아닌 대상 탐색 정책이다. 수확마다 수분 cache 전체를 무효화하는 비용은 밀집 농장에서 계측할 가치가 있지만 정확한 갱신 시점을 바꾸는 최적화는 하지 않았다.

새로 확정·수정해야 할 기능 결함은 이번 검토에서 발견하지 못했다. 아래는 구조 변경과 분리한 **미재현 위험 또는 기존 정책 한계**다.

1. Terrain grid signature에 지형 높이/revision이 포함되지 않는다. 고정된 시선에서 다른 플레이어가 지형을 바꿀 때 marker 높이가 늦게 갱신되는지 재현이 필요하다.
2. Scythe grown-prefab cache는 scene/개수 변경을 본다. 외부 모드가 같은 개수를 유지하며 내용을 교체하는 상황의 갱신은 미검증이다.
3. EWD bridge 초기 탐색 실패와 나중 로딩 시 재시도에 관한 경고 문구/플래그 경로는 실제 로드 순서에서 재현 여부를 확인해야 한다.
4. 기존 수확 receipt는 128개/30초/session-only이며 접속 종료나 지연 시 honey XP를 잃을 수 있다. 지속성 있는 재지급·자동 retry는 아이템 중복 및 동기화 정책 결정이 필요하다. client가 보낸 skill은 검증된 actor와 연결되지만 서버의 독립 anti-cheat 검증은 아니다.
5. 다른 모드가 아이템 일부 생성 뒤 예외를 던지는 경우까지 전역 원자성을 보장하지 않는다. 재료/아이템 소실·복제 관련 정책 변경을 이번 리팩터링에 섞지 않았다.

## 수행한 검증과 남은 실행 검증

| 단계 | 수행 결과 |
| --- | --- |
| 기준 빌드 | `dotnet build Groundwork.sln -c Debug -p:DeployToGame=true` 성공, 경고 0 / 오류 0 |
| 기준 자동 검사 | 1.0.15 클라이언트/서버 각각 직접 게임·Unity 멤버 778개와 Harmony 대상 76개 통과, 실제 Unity Mono 6.13.0 관리 검사 63개 통과 |
| 변경별 | 각 코드 커밋 전 Debug 병합 빌드와 client 정적/Mono 검사, diff 검토 및 배포 DLL 해시 대조 통과. 첫 단계의 새 7개 검사까지 포함하면 70개이며 변경 전 DLL도 같은 70개를 통과 |
| 최종 자동 검사 | 1.0.14 및 1.0.15의 클라이언트/서버 원본 4조합 각각 멤버 778개 / Harmony 대상 76개 / Mono 검사 70개 통과 |
| 외부 표면 대조 | 기준·최종 DLL의 공개 타입/멤버 38개 항목, Harmony 선언/메서드 222개 항목, 포함 리소스 4개 이름·바이트 해시 및 assembly references 동일. 설정·저장 관련 소스 diff 없음 |
| 최종 병합 | GroundworkPlugin, internalized ServerSync.ConfigSync 및 YamlDotNet Deserializer 타입 존재 확인 |
| 배포 | `bin/Debug/Groundwork.dll`과 Steam `Valheim/BepInEx/plugins/Groundwork.dll` SHA-256 모두 `D4618AF2786738C4A5BF5B2C888A84FCC6DE472B8EAC30AF69E8AD186B3BAC25` |

원본 DLL 정적 검사는 참조 존재/접근 제한/패치 선언을 확인한다. Mono 검사는 원본 관리 코드의 serialization, invalid packet, request/receipt 중복·만료/중첩 scope, 원본 IL 기반 transpiler 변환 및 managed grid snapshot을 확인한다. Unity 엔진 장면, plugin Awake, 실제 Harmony detour 적용, 네트워크 소켓, 생성/파괴, 아이템 생성·revision 저장을 실행한 것은 아니다. 실패를 숨기거나 이 검사들을 게임 실행 통과로 보고하지 않았다.

후속 실제 실행은 백업한 월드에서 다음 순서로 수행한다.

1. 클라이언트/호스트: Hoe/Cultivator Grid↔Vanilla, 범위 변경, 여러 TerrainOp, 배치 성공/실패/중간 예외. 프리뷰 위치 및 재료·스태미나·내구도 비용이 기존과 같은지 확인한다.
2. 일반/대량 재배: 등록 안 된 자연산, plantable false, 정상 규칙, Biomes 생략/유효/잘못된 이름, EWD 및 YAML live 변경. 기존 설치물 보존과 배치 차단을 확인한다.
3. 벌집: Off/Compact/Detailed, 밤/비 각각 및 동시, x1/0/0.999 경계, 영어·한국어. 기존 줄·공백·색상과 생산량이 그대로인지 확인한다.
4. 전용 서버+두 클라이언트: owner와 수확자가 다른 경우, 낮은 스킬 포함, 중복/지연 RPC, owner 이동, zone unload, 저장/재접속 및 ZenBeehive/Jewelcrafting/EWD 조합. 기존 `HarvestSkillVerification.md`의 아이템·XP 체크를 포함한다. 이번에는 수행하지 않았다.

구현 최종 커밋은 `67b1e42`이며 이 문서는 후속 문서 커밋으로 보관한다. 모드 버전 1.1.13과 의존성은 그대로다. 새 브랜치/worktree, push, Release 빌드/ZIP 또는 사이트 게시를 수행하지 않았다.
