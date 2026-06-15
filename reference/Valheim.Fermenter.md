# Valheim `Fermenter` Component Reference

## 분석 기준

- 코드 기준: `C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll`
- publicized 보조 기준: `C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\publicized_assemblies\assembly_valheim_publicized.dll`
- prefab 기준: SoftRef bundle `c4210710`, `Assets/GameElements/Pieces/fermenter.prefab`
- 확인일: 2026-06-15

원본 assembly의 `Fermenter`는 `MonoBehaviour`, `Hoverable`, `Interactable`을 구현한다. publicized assembly에서는 private 멤버 접근성이 public으로 바뀌어 있지만, 확인한 로직은 원본 assembly와 동일하다.

## 역할 요약

`Fermenter`는 발효 가능한 base item 1개를 받아, 네트워크 시간 기준으로 일정 시간이 지난 뒤 결과 item 여러 개를 배출하는 제작 설비다. 내용물과 시작 시간은 ZDO에 저장되며, add/tap RPC는 ZDO owner에서 검증 후 처리된다.

발효 진행에는 지붕/노출 판정이 중요하다. 발효 중 `m_hasRoof == false` 또는 `m_exposed == true`가 되면 owner가 start timer를 현재 시간으로 계속 reset한다. 즉, 조건이 나쁜 동안은 발효가 pause되는 것이 아니라 진행 시간이 계속 새로 시작된다.

## Prefab Serialized 값

`fermenter.prefab`의 `Fermenter` MonoBehaviour에서 확인한 값이다.

| 필드 | 값 |
| --- | --- |
| `m_name` | `$piece_fermenter` |
| `m_fermentationDuration` | `2400.0` seconds, 40분 |
| `m_fermentingObject` | `_fermenting` GameObject |
| `m_readyObject` | `_ready` GameObject |
| `m_topObject` | `_top` GameObject |
| `m_addSwitch` | `Switch` component reference |
| `m_tapSwitch` | `Switch` component reference |
| `m_tapDelay` | `2.5` seconds |
| `m_outputPoint` | `output` transform |
| `m_roofCheckPoint` | `roofcheckpoint` transform |
| `m_addedEffects` | `sfx_fermenter_add`, `vfx_fermenter_add` |
| `m_tapEffects` | `sfx_fermenter_tap`, `vfx_fermenter_tap` |
| `m_spawnEffects` | `sfx_lootspawn`, `vfx_lootspawn` |

Prefab의 주요 companion component는 `Piece`, `ZNetView`, `LODGroup`, `WearNTear`, `Fermenter`이다.

## 변환 목록

기본 prefab의 `m_conversion` 목록이다. `m_from.gameObject.name`이 ZDO `Content`에 저장되는 값이며, item 표시명은 `m_from.m_itemData.m_shared.m_name` 토큰을 사용한다.

| From prefab | From name token | To prefab | To name token | Produced |
| --- | --- | --- | --- | --- |
| `MeadBaseHealthMinor` | `$item_meadbasehealth` | `MeadHealthMinor` | `$item_mead_hp_minor` | 6 |
| `MeadBaseHealthMedium` | `$item_meadbasehealth_medium` | `MeadHealthMedium` | `$item_mead_hp_medium` | 6 |
| `MeadBaseStaminaMinor` | `$item_meadbasestamina` | `MeadStaminaMinor` | `$item_mead_stamina_minor` | 6 |
| `MeadBaseStaminaMedium` | `$item_meadbasestamina_medium` | `MeadStaminaMedium` | `$item_mead_stamina_medium` | 6 |
| `MeadBasePoisonResist` | `$item_meadbasepoisonresist` | `MeadPoisonResist` | `$item_mead_poisonres` | 6 |
| `MeadBaseFrostResist` | `$item_meadbasefrostresist` | `MeadFrostResist` | `$item_mead_frostres` | 6 |
| `BarleyWineBase` | `$item_barleywinebase` | `BarleyWine` | `$item_barleywine` | 6 |
| `MeadBaseTasty` | `$item_meadbasetasty` | `MeadTasty` | `$item_mead_tasty` | 6 |
| `MeadBaseHealthMajor` | `$item_meadbasehealth_major` | `MeadHealthMajor` | `$item_mead_hp_major` | 6 |
| `MeadBaseStaminaLingering` | `$item_meadbasestamina_lingering` | `MeadStaminaLingering` | `$item_mead_stamina_lingering` | 6 |
| `MeadBaseEitrMinor` | `$item_meadbaseeitr` | `MeadEitrMinor` | `$item_mead_eitr_minor` | 6 |
| `MeadBaseEitrLingering` | `$item_meadbaseeitr_lingering` | `MeadEitrLingering` | `$item_mead_eitr_lingering` | 6 |
| `MeadBaseHealthLingering` | `$item_meadbasehealth_lingering` | `MeadHealthLingering` | `$item_mead_hp_lingering` | 6 |
| `MeadBaseBzerker` | `$item_meadbasebzerker` | `MeadBzerker` | `$item_mead_bzerker` | 3 |
| `MeadBaseStrength` | `$item_meadbasestrength` | `MeadStrength` | `$item_mead_strength` | 6 |
| `MeadBaseHasty` | `$item_meadbasehasty` | `MeadHasty` | `$item_mead_hasty` | 6 |
| `MeadBaseLightFoot` | `$item_meadbaselightfoot` | `MeadLightfoot` | `$item_mead_lightfoot` | 6 |
| `MeadBaseSwimmer` | `$item_meadbaseswimmer` | `MeadSwimmer` | `$item_mead_swimmer` | 6 |
| `MeadBaseTamer` | `$item_meadbasetamer` | `MeadTamer` | `$item_mead_tamer` | 6 |
| `MeadBaseBugRepellent` | `$item_meadbasebugrepellent` | `MeadBugRepellent` | `$item_mead_bugrepellent` | 6 |

## ZDO 상태

| ZDOVars | 실제 stable string | 용도 |
| --- | --- | --- |
| `ZDOVars.s_content` | `Content` | 현재 들어 있는 base item prefab name |
| `ZDOVars.s_startTime` | `StartTime` | 발효 시작 시각의 `DateTime.Ticks` |

`Content`가 비어 있으면 empty 상태다. `StartTime == 0`이면 `GetFermentationTime()`은 `-1.0`을 반환한다.

## 상태 모델

내부 enum은 다음 값을 가진다.

```csharp
private enum Status
{
    Empty,
    Fermenting,
    Exposed,
    Ready
}
```

하지만 현재 `GetStatus()`는 `Exposed`를 반환하지 않는다.

```csharp
if (string.IsNullOrEmpty(GetContent())) return Empty;
if (GetFermentationTime() > m_fermentationDuration) return Ready;
return Fermenting;
```

노출 상태는 enum 상태가 아니라 `m_exposed`와 `m_hasRoof` boolean으로 별도 관리된다. `Status.Exposed` case는 `SlowUpdate()`에 남아 있지만 실질적으로 도달하지 않는다.

## Lifecycle

### `Awake()`

1. `ZNetView`를 캐시한다.
2. `_fermenting`, `_ready`는 비활성화하고 `_top`은 활성화한다.
3. ZDO가 있으면 `"RPC_AddItem"`과 `"RPC_Tap"`을 등록한다.
4. 이미 발효 중이면 `SlowUpdate()`를 2초 후부터 2초 간격으로 호출한다.
5. 그 외 상태면 `SlowUpdate()`를 즉시 시작하고 2초 간격으로 호출한다.
6. `WearNTear.m_onDestroyed`에 `OnDestroyed()`를 등록한다.

### `SlowUpdate()`

`SlowUpdate()`는 매 2초마다 호출된다.

1. `UpdateCover(2f)`를 호출한다.
2. 상태에 따라 visual object를 갱신한다.

| 상태 | Visual 처리 |
| --- | --- |
| `Empty` | `_fermenting=false`, `_ready=false`, `_top=false` |
| `Fermenting` | `_ready=false`, `_top=true`, `_fermenting=!m_exposed && m_hasRoof` |
| `Ready` | `_fermenting=false`, `_ready=true`, `_top=true` |

## Roof / Exposure 판정

`UpdateCover(float dt, bool forceUpdate = false)`는 내부 timer로 10초마다 cover를 재계산한다. `Interact()`에서는 `forceUpdate: true`로 즉시 갱신한다.

```csharp
Cover.GetCoverForPoint(m_roofCheckPoint.position, out coverPercentage, out underRoof);
m_exposed = coverPercentage < 0.7f;
m_hasRoof = underRoof;

if ((m_exposed || !m_hasRoof) && m_nview.IsOwner())
{
    ResetFermentationTimer();
}
```

해석:

- `underRoof`는 위쪽 sphere cast가 non-leaky collider를 맞췄는지 여부다.
- `m_exposed`는 cover percentage가 70% 미만이면 true다.
- 발효 중 조건이 나쁘면 `StartTime`을 현재 네트워크 시간으로 reset한다.
- 이미 `Ready` 상태가 된 뒤에는 `ResetFermentationTimer()`가 동작하지 않는다.

## Hover와 Interact

### `GetHoverText()`

- PrivateArea 접근 권한이 없으면 `$piece_noaccess`.
- `Ready`: content name과 `$piece_fermenter_ready`, Use 키로 tap 안내.
- `Fermenting`: 지붕 없음이면 `$piece_fermenter_needroof`, 노출이면 `$piece_fermenter_exposed`, 정상이면 `$piece_fermenter_fermenting`.
- `Empty`: empty 문구에 지붕/노출 경고를 덧붙이고 Use 키로 add 안내.

Hover의 roof/exposure 문구는 마지막 `UpdateCover()` 결과에 의존한다. `Interact()`는 force update를 먼저 하지만 hover 자체는 직접 force update를 하지 않는다.

### `Interact(Humanoid user, bool hold, bool alt)`

- hold 반복 입력이면 `false`.
- `UpdateCover(0f, forceUpdate: true)`를 먼저 수행한다.
- PrivateArea 접근 권한이 없으면 `true`.
- `Empty`:
  1. 지붕 없음이면 `$piece_fermenter_needroof`, `false`
  2. 노출이면 `$piece_fermenter_exposed`, `false`
  3. inventory에서 변환 가능한 item을 찾지 못하면 `$msg_noprocessableitems`, `false`
  4. 찾으면 `AddItem(user, itemData)`, `true`
- `Ready`: `"RPC_Tap"` 호출 후 `true`.
- `Fermenting`: `false`.

### `UseItem(Humanoid user, ItemDrop.ItemData item)`

PrivateArea 접근 권한만 확인하고 곧바로 `AddItem()`을 호출한다. `Interact()` 경로와 달리 roof/exposure 조건을 직접 검사하지 않는다. 따라서 item 직접 사용 경로에서는 조건이 나쁜 fermenter에도 base item이 들어갈 수 있다. 다만 발효 중에는 `UpdateCover()`가 `StartTime`을 계속 reset하므로 정상 조건을 만족하기 전까지 완료되지 않는다.

## Add 흐름

`AddItem()`은 local에서 다음 조건을 검사한다.

1. 현재 상태가 `Empty`.
2. `IsItemAllowed(item)`이 true.
3. `user.GetInventory().RemoveOneItem(item)`이 성공.

성공하면 owner에게 `"RPC_AddItem"`을 호출하면서 `item.m_dropPrefab.name`을 전달한다.

`RPC_AddItem(long sender, string name)`은 owner에서 다시 검사한다.

- owner가 아니면 무시.
- 현재 상태가 `Empty`가 아니면 무시.
- `IsItemAllowed(name)`이 false면 dev log `"Item not allowed"` 후 무시.
- 성공 시 add effect를 만들고 ZDO에 `Content=name`, `StartTime=ZNet.instance.GetTime().Ticks`를 저장한다.

클라이언트와 서버/owner의 conversion list가 mod로 달라지면 local item removal 이후 owner가 RPC를 거부할 수 있다. conversion list를 바꾸는 mod는 양쪽 동기화를 특히 조심해야 한다.

## Tap / Output 흐름

`RPC_Tap(long sender)`는 owner이고 현재 상태가 `Ready`일 때만 동작한다.

1. 현재 `Content`를 `m_delayedTapItem`에 저장한다.
2. `m_tapDelay` 후 `DelayedTap()`을 예약한다. 기본 prefab 값은 2.5초다.
3. tap effect를 생성한다.
4. ZDO `Content`를 empty로, `StartTime`을 0으로 즉시 reset한다.

`DelayedTap()`은 다음을 수행한다.

1. `m_spawnEffects`를 `m_outputPoint.position`에 생성한다.
2. `m_delayedTapItem`에 맞는 `ItemConversion`을 찾는다.
3. `m_producedItems` 수만큼 결과 item을 `m_outputPoint.position + Vector3.up * 0.3f`에 instantiate한다.
4. 각 item에 `ItemDrop.OnCreateNew()`를 호출한다.

Fermenter output은 `Game.instance.ScaleDrops()`를 사용하지 않는다. prefab의 `m_producedItems` 수량이 그대로 생성된다.

## 파괴 시 동작

`Fermenter`에는 `DropAllItems()`가 존재한다. 이 메서드는 직접 호출될 경우 다음처럼 동작한다.

- `Ready` 상태면 conversion 결과물 `m_to`를 `m_producedItems`만큼 드롭한다.
- `Ready`가 아니면 현재 `Content` prefab을 찾아 원본 base item 1개를 드롭한다.
- 이후 ZDO `Content`와 `StartTime`을 reset한다.

하지만 현재 원본 assembly와 IL 기준의 `OnDestroyed()`는 다음과 같다.

```csharp
private void OnDestroyed()
{
    m_nview.IsOwner();
}
```

IL에서도 `IsOwner()` 호출 결과를 `pop`하고 return한다. 즉, `WearNTear.m_onDestroyed`에는 등록되지만 현 build의 `OnDestroyed()`는 `DropAllItems()`를 호출하지 않는다. 파괴 시 내용물을 반환하는 동작을 기대하는 mod라면 이 부분을 직접 patch하거나 별도 처리해야 한다.

## 모딩/패치 주의점

- 원본 assembly에서 대부분의 helper 메서드와 `Status` enum은 private이다. publicized assembly에서는 public으로 보일 수 있으나 runtime target은 원본 접근성을 기준으로 생각하는 편이 안전하다.
- `Status.Exposed`는 사실상 dead state다. 노출 여부는 `m_exposed`와 `m_hasRoof`로 판단한다.
- 완료 판정은 `GetFermentationTime() > m_fermentationDuration`이다. 정확히 2400.0초에서는 아직 `Ready`가 아니다.
- 발효 중 roof/exposure가 나쁘면 timer가 pause되는 것이 아니라 현재 시간으로 reset된다.
- `UseItem()`은 roof/exposure 조건을 직접 막지 않는다.
- `FindCookableItem()`은 `m_conversion` 순서대로 inventory에서 `m_from.m_itemData.m_shared.m_name`에 해당하는 item을 찾는다. 여러 base item이 있으면 conversion list 순서가 우선순위다.
- `RPC_Tap()`은 output 생성 전에 ZDO content를 비운다. tap delay 중 object lifecycle을 건드리는 patch는 결과물 유실 가능성을 고려해야 한다.
