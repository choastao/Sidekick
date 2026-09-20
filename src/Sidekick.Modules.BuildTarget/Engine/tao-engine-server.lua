-- TAO 引擎 helper：把 PoB2 无头引擎包成一个 stdio 上的 JSON-Lines 服务
--
-- 配套：主程序侧 Sidekick.Modules.BuildTarget/Services/PobEngineClient.cs
--   · 请求 {"id":N,"method":"...","params":{...}}
--   · 应答 {"id":N,"ok":true,"result":{...}} 或 {"id":N,"ok":false,"error":"..."}
--
-- ⚠ 运行前提（踩过才知道，别省）：
--   1) **工作目录必须是 <引擎目录>/pob/src** —— HeadlessWrapper.lua 第一行就
--      dofile("_SimpleGraphic.def.lua")，是相对路径。
--   2) 本脚本路径由主程序用**绝对路径**传入（arg[0]），因为工作目录不是它所在目录。
--   3) package.path 要补 <引擎>/pob/runtime/lua/?.lua（dkjson / xml / base64 / sha1 在那儿）。
--
-- 试穿用的是 PoB 自己的对比机制：`calcsTab:GetMiscCalculator()` 返回的函数
-- 接受 { repSlotName = <槽位名>, repItem = <Item> }，**只替换某槽位算一次、不动 BD**。
-- 槽位名见 Classes/ItemsTab.lua 的 baseSlots（"Weapon 1"/"Helmet"/"Body Armour"/"Flask 1"/"Charm 1"…）。

local selfPath = (arg and arg[0]) or "tao-engine-server.lua"
local BASE = selfPath:match("^(.*)[/\\][^/\\]*$") or "."

-- 引擎载荷的目录名：**分发包里是 `pob/`，开发期是 clone 出来的 `repo/`** —— 两个都认，
-- 否则换个布局就得改代码。
local function fileExists(path)
    local handle = io.open(path, "rb")
    if handle then
        handle:close()
        return true
    end
    return false
end

local sub = nil
for _, candidate in ipairs({ "pob", "repo" }) do
    if fileExists(BASE .. "/" .. candidate .. "/src/HeadlessWrapper.lua") then
        sub = candidate
        break
    end
end

local ENGINE_DIR = BASE .. "/" .. (sub or "pob")

package.path = table.concat({
    package.path,
    ENGINE_DIR .. "/runtime/lua/?.lua",
    ENGINE_DIR .. "/runtime/lua/?/init.lua",
}, ";")

-- 引擎载入（会打印若干 Loading... 到 stdout —— 主程序只认 '{"id"' 开头的行，忽略这些噪音）
dofile("HeadlessWrapper.lua")

local dkjson = require("dkjson")

local state = {
    base = nil,      -- 基准 BD 的数值快照
    calcFunc = nil,  -- calcsTab:GetMiscCalculator()
}

local function out(tbl)
    io.write(dkjson.encode(tbl), "\n")
    io.flush()
end

local function respond(id, result) out({ id = id, ok = true, result = result }) end
local function fail(id, message) out({ id = id, ok = false, error = tostring(message) }) end

local function statsFrom(output)
    return {
        dps = output.TotalDPS,
        ehp = output.TotalEHP,
        life = output.Life,
        energyShield = output.EnergyShield,
        mana = output.Mana,
        combinedDps = output.CombinedDPS,
        fullDps = output.FullDPS,
        -- 保命能力的分项，界面想展开时用得上
        physicalDotEhp = output.PhysicalDotEHP,
        fireDotEhp = output.FireDotEHP,
        coldDotEhp = output.ColdDotEHP,
        lightningDotEhp = output.LightningDotEHP,
        chaosDotEhp = output.ChaosDotEHP,
    }
end

local handlers = {}

handlers.ping = function()
    return { pong = true, engine = "path-of-building-poe2", buildLoaded = state.base ~= nil }
end

-- params: { xml = "<PathOfBuilding2>…", name = "…" }
handlers.load_build = function(params)
    local xml = params.xml
    if type(xml) ~= "string" or #xml == 0 then
        error("params.xml is required")
    end

    loadBuildFromXML(xml, params.name or "tao-base")
    if build == nil then
        error("engine did not expose a build object")
    end

    state.calcFunc = build.calcsTab:GetMiscCalculator()
    state.base = statsFrom(build.calcsTab.mainOutput)
    return { stats = state.base }
end

-- 基准数值（没有基准 BD 时给空表，主程序按"未载入"处理）
handlers.stats = function()
    if state.base == nil then
        return { loaded = false }
    end

    return { loaded = true, stats = statsFrom(build.calcsTab.mainOutput), base = state.base }
end

-- params: { slot = "Helmet", item = "<PoB 格式物品文本>" }
-- 返回该物品放进这个槽位后的数值（与 base 的差由主程序算）。
handlers.equip = function(params)
    if state.calcFunc == nil then
        error("no build loaded")
    end

    local slot, itemText = params.slot, params.item
    if type(slot) ~= "string" or #slot == 0 then
        error("params.slot is required")
    end
    if type(itemText) ~= "string" or #itemText == 0 then
        error("params.item is required")
    end

    local item = new("Item"):Item(itemText)
    item:BuildAndParseRaw()

    local output = state.calcFunc({ repSlotName = slot, repItem = item })
    if output == nil then
        error("engine returned no output for slot " .. slot)
    end

    -- 「引擎不认识这条词缀」：PoB 解析不出 modList 的行会把**整行原文**写进 modLine.extra
    -- （Classes/Item.lua:1360-1364），ItemTools.lua:350 就是拿 extra 标 UNSUPPORTED 的。
    -- 透传给主程序 = 消灭界面上「收益 0 是这条词缀没贡献，还是引擎压根没算它」的歧义。
    local unsupported = {}
    for _, list in ipairs({ item.implicitModLines, item.explicitModLines, item.enchantModLines, item.runeModLines }) do
        for _, modLine in ipairs(list or {}) do
            -- 形状意外的行（不是表）不该让整个 equip 变成「引擎出错」：跳过就好。
            if type(modLine) == "table" and modLine.extra then
                unsupported[#unsupported + 1] = modLine.line or tostring(modLine.extra)
            end
        end
    end

    return {
        stats = statsFrom(output),
        unsupported = { count = #unsupported, lines = unsupported },
    }
end

-- 设备/槽位清单，界面用来做映射（省得主程序硬编码）
handlers.slots = function()
    local list = {}
    for _, slot in ipairs(build.itemsTab.orderedSlots or {}) do
        if slot.slotName then
            list[#list + 1] = slot.slotName
        end
    end

    return { slots = list }
end

handlers.shutdown = function()
    return { bye = true }
end

-- ---- 主循环：一行一个请求 ----
while true do
    local line = io.read("*l")
    if line == nil then
        break -- 父进程关掉了 stdin
    end

    if line:match("%S") then
        local ok, request = pcall(dkjson.decode, line)
        if not ok or type(request) ~= "table" or request.method == nil then
            fail(request and request.id, "malformed request")
        else
            local handler = handlers[request.method]
            if handler == nil then
                fail(request.id, "unknown method: " .. tostring(request.method))
            else
                local called, result = pcall(handler, request.params or {})
                if called then
                    respond(request.id, result)
                    if request.method == "shutdown" then
                        break
                    end
                else
                    fail(request.id, result)
                end
            end
        end
    end
end
