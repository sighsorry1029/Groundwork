# Valheim `Beehive` Component Reference

## 분석 기준

- 코드 기준: `C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll`
- publicized 보조 기준: `C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\publicized_assemblies\assembly_valheim_publicized.dll`
- prefab 기준: SoftRef bundle `c4210710`, `Assets/GameElements/Pieces/piece_beehive.prefab`
- 확인일: 2026-06-15

원본 assembly의 `Beehive`는 `MonoBehaviour`, `Hoverable`, `Interactable`을 구현한다. publicized assembly에서는 private 멤버 접근성이 public으로 바뀌어 있지만, 확인한 로직은 원본 assembly와 동일하다.

## 역할 요약

`Beehive`는 설치된 벌집의 벌 효과 표시, 꿀 생산량 누적, 플레이어 상호작용, 꿀 드롭 생성을 담당한다. 생산 상태는 `ZNetView`의 ZDO에 저장되며, 실제 생산 누적과 추출 RPC 처리는 ZDO owner가 수행한다.

핵심 조건은 두 가지다.

- 현재 위치 biome이 `m_biome` 비트마스크에 포함되어야 한다.
- `m_coverPoint`의 cover percentage가 `m_maxCover`보다 작아야 한다.

낮 여부는 기본 prefab에서 벌 이펙트 표시와 빈 벌집 확인 메시지에만 영향을 준다. 생산 누적 자체는 daylight 조건을 보지 않는다.

## Prefab Serialized 값

`piece_beehive.prefab`의 `Beehive` MonoBehaviour에서 확인한 값이다.

| 필드 | 값 |
| --- | --- |
| `m_name` | `$piece_beehive` |
| `m_coverPoint` | `coverpoint` transform |
| `m_spawnPoint` | `spawnpoint` transform |
| `m_beeEffect` | `BeeEffect` GameObject |
| `m_effectOnlyInDaylight` | `true` |
| `m_maxCover` | `0.6` |
| `m_biome` | `25`, 즉 `Meadows | BlackForest | Plains` |
| `m_secPerUnit` | `1200.0` seconds, 꿀 1개당 20분 |
| `m_maxHoney` | `4` |
| `m_honeyItem` | `Honey` / `$item_honey`, max stack 50, weight 0.2 |
| `m_spawnEffect` | `sfx_pickable_pick`, `vfx_pickable_pick` |
| `m_extractText` | `$piece_beehive_extract` |
| `m_checkText` | `$piece_beehive_check` |
| `m_areaText` | `$piece_beehive_area` |
| `m_freespaceText` | `$piece_beehive_freespace` |
| `m_sleepText` | `$piece_beehive_sleep` |
| `m_happyText` | `$piece_beehive_happy` |
| `m_notConnectedText` | empty |
| `m_blockedText` | empty |

Prefab의 주요 companion component는 `Piece`, `ZNetView`, `WearNTear`, `Beehive`, `SpawnOnDamaged`, `LODGroup`이다.

## ZDO 상태

`Beehive`가 사용하는 ZDO key는 다음과 같다.

| ZDOVars | 실제 stable string | 용도 |
| --- | --- | --- |
| `ZDOVars.s_lastTime` | `lastTime` | 마지막 생산 업데이트 시각의 `DateTime.Ticks` |
| `ZDOVars.s_level` | `level` | 현재 저장된 꿀 개수 |
| `ZDOVars.s_product` | `product` | 다음 꿀 생산까지 누적된 초 단위 진행량 |

`Awake()`에서 owner이고 `lastTime == 0`이면 현재 `ZNet.instance.GetTime().Ticks`로 초기화한다.

## Lifecycle

### `Awake()`

1. `ZNetView`, child `Collider`, `Piece`를 캐시한다.
2. ZDO가 없으면 이후 초기화를 하지 않는다.
3. owner이고 `lastTime`이 비어 있으면 현재 네트워크 시간을 기록한다.
4. `"RPC_Extract"`를 등록한다.
5. `UpdateBees()`를 0초 후부터 10초 간격으로 반복 호출한다.

### `UpdateBees()`

```csharp
bool flag = CheckBiome() && HaveFreeSpace();
bool active = flag && (!m_effectOnlyInDaylight || EnvMan.IsDaylight());
m_beeEffect.SetActive(active);

if (m_nview.IsOwner() && flag)
{
    float dt = GetTimeSinceLastUpdate();
    float product = zdo.GetFloat(ZDOVars.s_product);
    product += dt;
    if (product > m_secPerUnit)
    {
        int add = (int)(product / m_secPerUnit);
        IncreseLevel(add);
        product = 0f;
    }
    zdo.Set(ZDOVars.s_product, product);
}
```

중요한 세부사항:

- `flag`가 false면 `lastTime`을 갱신하지 않는다. 막힌 상태가 풀리면 막혀 있던 시간까지 `GetTimeSinceLastUpdate()`에 포함될 수 있다.
- 생산 조건은 biome과 free space뿐이다. 밤에는 `BeeEffect`가 꺼질 수 있지만, owner 생산 누적은 계속된다.
- `product > m_secPerUnit`일 때만 생산한다. 정확히 같은 값이면 다음 update까지 기다린다.
- 생산이 발생하면 남은 remainder를 보존하지 않고 `product = 0f`로 버린다.
- 꿀 개수는 `Mathf.Clamp(..., 0, m_maxHoney)`로 0-4 사이에 고정된다.

## 조건 판정

### Biome

`CheckBiome()`은 현재 위치의 biome과 `m_biome`을 bitwise AND 한다.

```csharp
(Heightmap.FindBiome(transform.position) & m_biome) != 0
```

기본 prefab의 `m_biome = 25`는 다음 biome을 허용한다.

- `Meadows` = 1
- `BlackForest` = 8
- `Plains` = 16

### Free Space / Cover

`HaveFreeSpace()`는 `Cover.GetCoverForPoint(m_coverPoint.position, out coverPercentage, out _)`를 호출하고 `coverPercentage < m_maxCover`인지 본다.

기본 prefab에서는 `m_maxCover = 0.6`이다. `Cover`는 17개 방향 ray를 사용해 cover percentage를 계산하므로, 대략 60% 이상 막힌 것으로 판정되면 벌집이 불만 상태가 된다.

## Hover와 Interact

### `GetHoverText()`

- PrivateArea 접근 권한이 없으면 `$piece_noaccess`.
- 꿀이 1개 이상이면 `m_honeyItem` 이름과 현재 개수를 표시하고 Use 키로 추출 안내.
- 꿀이 없으면 empty 표시와 check 안내.

### `Interact(Humanoid character, bool repeat, bool alt)`

- repeat 상호작용은 무시하고 `false`.
- PrivateArea 접근 권한이 없으면 아무 작업 없이 `true`.
- 꿀이 있으면 `Extract()` 호출 후 `PlayerStatType.BeesHarvested`를 1 증가.
- 꿀이 없으면 다음 순서로 메시지를 출력한다.
  1. biome 불일치: `m_areaText`
  2. free space 부족: `m_freespaceText`
  3. 밤이고 `m_effectOnlyInDaylight`: `m_sleepText`
  4. 그 외 정상: `m_happyText`

`UseItem()`은 항상 `false`를 반환한다. 벌집은 아이템 사용 상호작용을 받지 않는다.

## 추출 흐름

`Extract()`는 `"RPC_Extract"`를 owner에게 호출한다.

`RPC_Extract(long caller)`는 현재 꿀 개수가 1 이상일 때만 동작한다.

1. `m_spawnEffect`를 `m_spawnPoint.position`에 생성한다.
2. 현재 `honeyLevel`만큼 반복한다.
3. 각 꿀은 `m_spawnPoint.position` 기준 반경 0.5의 랜덤 XZ offset과 `0.25f * i`의 Y offset을 받는다.
4. `m_honeyItem`을 instantiate한다.
5. 생성된 `ItemDrop`의 stack은 `Game.instance.ScaleDrops(m_honeyItem.m_itemData, 1)`로 설정된다.
6. `level`을 0으로 reset한다.

`ScaleDrops`를 사용하므로 world resource rate가 1이 아니면 꿀 하나당 stack 수가 조정될 수 있다. 예를 들어 resource rate가 2라면 각 honey drop stack이 2가 될 수 있다.

## 모딩/패치 주의점

- 원본 assembly에서 생산/상태 메서드는 대부분 private이다. publicized assembly를 참조하면 접근은 쉬워지지만, Harmony patch target은 원본 signature 기준으로 보는 편이 안전하다.
- `IncreseLevel`은 오타가 있는 실제 메서드명이다. patch 시 `IncreaseLevel`이 아니다.
- `m_connectedObject`, `m_blockingPiece`, `m_notConnectedText`, `m_blockedText`는 현재 `Beehive` 로직에서 사용되지 않는다.
- `HaveFreeSpace()` 실패 중에는 `lastTime`이 갱신되지 않는다. 생산을 완전히 멈추는 시스템을 만들려면 막힘 상태에서도 시간을 갱신하거나 별도 ZDO key를 사용해야 한다.
- `RPC_Extract`는 caller 권한을 별도로 검사하지 않고, 추출 가능 여부는 현재 꿀 개수에만 의존한다. 접근 권한은 일반 `Interact()` 경로에서 먼저 체크된다.
- `m_product`는 추출 시 reset되지 않는다. 추출 직전 누적 중이던 partial progress는 유지될 수 있다.
