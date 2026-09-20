# PoB2 无头引擎探测报告（TAO 用）

日期：2026-09-20　执行者：Hermes（子代理首轮 10 分钟超时，由主线接手完成）
目标：验证 PoB2 引擎能否在本机 headless 跑通、单次重算多少毫秒、DPS/EHP 从哪个字段取。

## 结论：跑通了，且数值与 PoB 自己存的一致

| 项 | 结果 |
|---|---|
| 引擎 | `PathOfBuildingCommunity/PathOfBuilding-PoE2`（clone 到 `pob2-engine/repo`，sparse 含 `src/` + `runtime/`） |
| 解释器 | **LuaJIT 2.1**（r46，Windows x64）→ `pob2-engine/luajit/luajit.exe` + `lua51.dll` |
| 入口 | `repo/src/HeadlessWrapper.lua` → `_SimpleGraphic.def.lua` → `Launch.lua` |
| **单次重算耗时** | **412 / 232 / 194 ms**（首次含 JIT 预热，稳态 ≈ 200 ms） |
| DPS 字段 | **`build.calcsTab.mainOutput.TotalDPS`** |
| EHP 字段 | **`build.calcsTab.mainOutput.TotalEHP`** |
| 附带可用 | `mainOutput.Life` / `CombinedDPS` / `WithBleedDPS` / `PhysicalDotEHP` 等 |

**对账（关键证据）**：测试用的 BD 是 poe.ninja 导出的一份真实 Blood Mage Lv99，
其 XML 里自带 PoB 当时算出的 `<PlayerStat stat="TotalDPS" value="7644240.6617248"/>`。
本机 headless 引擎重算后 `mainOutput.TotalDPS = 7644240.6617248` —— **逐位一致**，
说明引擎加载链与数据版本都对得上，不是"跑起来了但算的是别的"。

## 可复现命令

```bash
# 工作目录必须是 repo/src：HeadlessWrapper 用相对路径 dofile("_SimpleGraphic.def.lua")
cd C:/Users/Administrator/poe2-work/pob2-engine/repo/src
../../luajit/luajit.exe ../../tao-probe.lua     # 引擎载入 + 3 次重算计时 + dump 数值字段
../../luajit/luajit.exe ../../tao-probe2.lua    # 精确定位 DPS/EHP 字段名
```

## 踩到的坑（下一个会话直接用）

1. **工作目录 = `repo/src`**。`HeadlessWrapper.lua` 第一行就 `dofile("_SimpleGraphic.def.lua")`、
   再 `dofile("Launch.lua")`，全是相对路径；在别处跑会直接 `cannot open`。
2. **`package.path` 要补 `repo/runtime/lua/?.lua`**（dkjson / xml / base64 / sha1 / socket / lua-profiler 都在那儿，
   不在 `src/` 下）。少了它报 `module 'dkjson' not found`。
3. **`runtime-win32.zip` 里就有 `lua51.dll`**，但 `luajit.exe` 不在里面 —— 要另找 LuaJIT 二进制
   （本次用 Per-Terra/LuaJIT-Auto-Builds 的 r46 Windows x64 包）。
4. 启动日志里的 `missing node 24665` 之类是**被动树数据与当前版本不一致的既有噪音**，不影响计算。
5. `CI` 环境变量会关掉 ModCache 加载（`__mainObject__.continuousIntegrationMode = os.getenv("CI")`）——
   我们要**保留 ModCache**（词缀解析要用），所以别设 `CI=1`。

## 试穿机制（C1b 已联调通过）

**接口**（PoB 自己的对比机制，不动 BD、算完即弃）：

```lua
local calcFunc = build.calcsTab:GetMiscCalculator()
local item = new("Item"):Item(<PoB 格式物品文本>)   -- 注意：英文 raw 文本
item:BuildAndParseRaw()
local output = calcFunc({ repSlotName = "Helmet", repItem = item })
-- output.TotalDPS / output.TotalEHP …
```

服务端实现：`tao-engine-server.lua`（JSON-Lines over stdio，方法 `ping`/`load_build`/`stats`/`equip`/`slots`/`shutdown`）。
联调脚本：`tao-engine-test.py`（跑一次出全部数字）。

**实测（同一份 Blood Mage Lv99 BD）**：

| 动作 | 耗时 | 结果 |
|---|---|---|
| helper 冷启动（首次 ping，含引擎 + Data 加载） | **1493 ms** | 就绪 |
| `load_build`（基准 BD） | **435 ms** | DPS 7644241 / EHP 23554 / Life 2433 |
| `stats` | 0 ms | 与基线一致 |
| **试穿「原来那件头盔」** | **16 ms** | 与基线**逐位一致**（自校验 ✓） |
| **试穿「追加 +500 to maximum Life 的同款」** | **17 ms** | DPS **+98763** / EHP **+2123** ✓ |
| **试穿「Body Armour 那件 → Helmet 槽」** | **14 ms** | DPS **+1209873** ✓ 替换确实生效 |
| `shutdown` | 0 ms | 退出码 0 |

**结论：一次试穿 ≈ 15 ms**，比原先估的「零点几秒」快一个数量级 ——
`GetMiscCalculator` 走的是增量重算，不是整轮 loadBuildFromXML（后者 194~434 ms）。
所以 C1 的延迟风险基本消失：热键 → 出数字应当在几十毫秒级。

⚠ **物品文本必须是 PoB 的英文 raw 格式**。游戏剪贴板里是繁体中文，**中→英转换是 C1c 的必做项**
（可用 `data/poe2/{zh,en}/trade-stats.json` 的中英对照 + `affix-pool-coe.json` 里的英文词缀文本拼）。

⚠ 别用 `ItemsTab:CreateDisplayItemFromRaw()` 做试穿：它会**改动 displayItem**（有副作用），
`new("Item"):Item(raw)` + `BuildAndParseRaw()` 才是只读的用法（ItemsTab 内部自己也是这么用的）。

---

## 槽位名（PoB2 的权威列表，`Classes/ItemsTab.lua` 的 `baseSlots`）

```
Weapon 1 / Weapon 1 Swap / Weapon 2 / Weapon 2 Swap / Helmet / Body Armour / Gloves / Boots /
Amulet / Ring 1 / Ring 2 / Ring 3 / Belt / Charm 1 / Charm 2 / Charm 3 / Flask 1 / Flask 2 /
Arm 1 / Arm 2 / Leg 1 / Leg 2
```

→ 顺带回答了「PoE2 药剂/咒符几个槽」这个产品问题：**Flask 2 个、Charm 3 个**。
另外每件装备还有 6 个 `… Jewel Socket N`（珠宝槽），珠宝是镶嵌物、不是独立装备槽。

---

## 尚未验证（后面的活）

- **中→英物品文本转换**（C1c 必做，见上）。
- 极端 BD（技能组多、词缀多）下的耗时分布。
- 打包体积：引擎载荷（`src/Data` 46 MB + `src/Modules` + `runtime`）单独分发还是随包 ——
  用户对体积敏感，倾向「引擎作为可选下载，放 `%APPDATA%\sidekick\pob2\`，不塞进主包」。
