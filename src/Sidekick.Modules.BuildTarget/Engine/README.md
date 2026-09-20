# Engine（PoB2 无头引擎）— 这个目录是什么

把《流放之路2》的计算引擎（Path of Building PoE2 版）当**独立 helper 进程**接进来，
用来回答「这件装备换上以后，DPS / EHP 是提升还是下降」—— 这是静态数据算不出来的
（`increased` 同区相加、`more` 独立乘算、同乘区边际递减，强依赖具体 BD 结构）。

    TAO.exe ──stdio(JSON Lines)──▶ luajit.exe tao-engine-server.lua ──▶ PoB2 引擎（Lua）

## 本目录里有什么

| 文件 | 作用 |
|---|---|
| `tao-engine-server.lua` | helper 服务端：把引擎包成 `ping`/`load_build`/`stats`/`equip`/`slots`/`shutdown` 六个方法 |
| `tao-engine-test.py` | 联调脚本：起 helper、载入 BD、试穿三件（原物 / 加词缀的 / 错槽对照）并打印耗时与数字 |
| `PROBE-REPORT.md` | 探测报告：实测耗时、踩过的坑、PoB 槽位名权威列表 |

**引擎本体（几十 MB 的 Lua 数据 + LuaJIT）不放进本仓库、默认也不进分发包** ——
用户对体积敏感（主包已 ~96 MB）。引擎作为**可选组件**安装到：

```
%APPDATA%\sidekick\pob2\
├── luajit.exe            ← LuaJIT 2.1 (Windows x64)
├── lua51.dll
├── tao-engine-server.lua ← 本目录的文件（发布时会随程序一起复制到输出目录）
└── pob\                  ← PoB2 引擎载荷（见下）
    ├── src\              ← 含 HeadlessWrapper.lua / Launch.lua / Data\ / Modules\
    └── runtime\lua\      ← dkjson / xml / base64 / sha1 等 Lua 库
```

主程序按顺序找引擎：**程序目录下的 `pob2\`** → `%APPDATA%\sidekick\pob2\`；
两处都没有就显示「引擎未安装」，不猜路径（`PobEngineClient.FindEngineDirectory()`）。

## 怎么装引擎（三步）

```bash
# 1) 取 PoB2 源码（只取需要的目录，别全量 clone）
git clone --filter=blob:none --sparse --depth 1 \
    https://github.com/PathOfBuildingCommunity/PathOfBuilding-PoE2 pob
cd pob && git sparse-checkout set src runtime/lua

# 2) 把 src/ 与 runtime/ 放进 %APPDATA%\sidekick\pob2\pob\（把 clone 出来的 pob 目录整个搬过去即可）

# 3) 放 LuaJIT：LuaJIT 2.1 Windows x64 的 luajit.exe + lua51.dll
#    （例如 Per-Terra/LuaJIT-Auto-Builds 的构建产物）放到 %APPDATA%\sidekick\pob2\
```

## 跑一次验证

```bash
cd %APPDATA%\sidekick\pob2\pob\src          # ⚠ 工作目录必须是这里
..\..\luajit.exe ..\..\tao-engine-server.lua
# 然后在 stdin 里按行发 JSON：{"id":1,"method":"ping"}
```

或者直接跑联调脚本（Windows 侧 python）：

```bash
python tao-engine-test.py     # 需要 build.xml（一份 PoB 导出的 BD）在同级目录
```

## 两个必须记住的前提（踩过才知道）

1. **工作目录必须是 `<引擎>\pob\src`**：`HeadlessWrapper.lua` 第一行就
   `dofile("_SimpleGraphic.def.lua")`，用的是相对路径。
2. **`package.path` 要补 `<引擎>\pob\runtime\lua\?.lua`**：dkjson / xml / base64 / sha1 在那儿。
   少了它报 `module 'dkjson' not found`。`tao-engine-server.lua` 已自动处理这一条。

## 许可

PoB2 的 `LICENSE.md` 正文是 **MIT**（Copyright (c) 2016 David Gowor），**允许随包分发**，
条件是在包内保留版权声明。我们只在用户自己安装引擎时使用它；
真要随包分发时必须补一份第三方声明（`ReleaseNotes` / `说明.md` 里提一句）。
⚠ GitHub API 对它的 `license` 字段返回 `NOASSERTION`（文件名与头部格式导致），**别据此判定许可不明**。
