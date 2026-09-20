namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 常用词缀预设。
///
/// 关键词同时收 **英文 / 繁体 / 简体** 三种写法：
/// 国际服繁中补丁复制出来的是繁体（冰冷傷害），英文原版是英文，国服是简体。
/// 三种都收，任何客户端都能匹配上。
/// </summary>
public static class StatPresets
{
    public record Preset(string Key, string Label, List<string> Keywords);

    public static readonly List<Preset> All =
    [
        // ---- 生存 ----
        new("Life", "最大生命", ["to maximum Life", "最大生命"]),
        new("EnergyShield", "最大能量护盾", ["to maximum Energy Shield", "最大能量護盾", "最大能量护盾"]),
        new("Mana", "最大魔力", ["to maximum Mana", "最大魔力"]),
        new("IncreasedEnergyShield", "增加能量护盾", ["increased Energy Shield", "增加能量護盾", "增加能量护盾"]),
        new("IncreasedArmour", "增加护甲", ["increased Armour", "增加護甲", "增加护甲"]),
        new("Armour", "护甲值", ["to Armour", "護甲", "护甲"]),
        new("Evasion", "闪避值", ["Evasion Rating", "閃避值", "闪避值"]),
        new("BlockChance", "格挡率", ["Chance to Block", "格擋率", "格挡率"]),
        new("LifeRegen", "生命回复", ["Life Regeneration", "生命回復", "生命回复"]),
        new("ManaRegen", "魔力回复率", ["Mana Regeneration", "魔力回復", "魔力回复"]),
        new("StunThreshold", "眩晕门槛", ["Stun Threshold", "眩暈門檻", "眩晕门槛"]),
        new("Thorns", "荆棘", ["Thorns", "荊棘", "荆棘"]),

        // ---- 抗性 ----
        new("FireRes", "火焰抗性", ["to Fire Resistance", "火焰抗性"]),
        new("ColdRes", "冰冷抗性", ["to Cold Resistance", "冰冷抗性"]),
        new("LightningRes", "闪电抗性", ["to Lightning Resistance", "閃電抗性", "闪电抗性"]),
        new("ChaosRes", "混沌抗性", ["to Chaos Resistance", "混沌抗性"]),
        new("AllEleRes", "全部元素抗性", ["to all Elemental Resistances", "所有元素抗性", "全部元素抗性"]),
        new("MaxRes", "最大抗性上限", ["to Maximum Resistance", "最大抗性"]),

        // ---- 属性 ----
        new("Strength", "力量", ["to Strength", "力量"]),
        new("Dexterity", "敏捷", ["to Dexterity", "敏捷"]),
        new("Intelligence", "智慧", ["to Intelligence", "智慧"]),
        new("AllAttributes", "全部属性", ["to all Attributes", "全部屬性", "全部属性"]),
        // ⚠ 关键词不能只写 "Spirit"：PoE2 的区域内词缀「#% increased chance of Azmeri Spirits」、
        //   独特咒符的机制行「Possessed by Spirit Of The Owl for 20 seconds on use」都含这个词，
        //   会把 20 当成「精魂 +20」写进角色合计（导入 BD 时实测踩到，随咒符部位接入一起暴露）。
        //   游戏里真正的精魂词缀只有「+# to Spirit」/「#% increased Spirit」（繁体文本是「#精魂」/「增加#%精魂」）。
        new("Spirit", "精魂", ["to Spirit", "increased Spirit", "精魂"]),

        // ---- 伤害 / 输出 ----
        new("IncreasedPhysicalDamage", "增加物理伤害", ["increased Physical Damage", "增加物理傷害", "增加物理伤害"]),
        new("AddedPhysicalDamage", "附加物理伤害", ["Adds # to # Physical Damage", "附加#至#物理傷害", "附加#至#物理伤害"]),
        new("AddedFireDamage", "附加火焰伤害", ["Adds # to # Fire Damage", "附加#至#火焰傷害", "附加#至#火焰伤害"]),
        new("AddedColdDamage", "附加冰冷伤害", ["Adds # to # Cold Damage", "附加#至#冰冷傷害", "附加#至#冰冷伤害"]),
        new("AddedLightningDamage", "附加闪电伤害", ["Adds # to # Lightning Damage", "附加#至#閃電傷害", "附加#至#闪电伤害"]),
        new("IncreasedElementalDamage", "增加元素伤害", ["increased Elemental Damage", "增加元素傷害", "增加元素伤害"]),
        new("IncreasedFireDamage", "增加火焰伤害", ["increased Fire Damage", "增加火焰傷害", "增加火焰伤害"]),
        new("IncreasedColdDamage", "增加冰冷伤害", ["increased Cold Damage", "增加冰冷傷害", "增加冰冷伤害"]),
        new("IncreasedLightningDamage", "增加闪电伤害", ["increased Lightning Damage", "增加閃電傷害", "增加闪电伤害"]),
        new("IncreasedChaosDamage", "增加混沌伤害", ["increased Chaos Damage", "增加混沌傷害", "增加混沌伤害"]),
        new("IncreasedSpellDamage", "增加法术伤害", ["increased Spell Damage", "增加法術傷害", "增加法术伤害"]),
        new("IncreasedAttackDamage", "增加攻击伤害", ["increased Attack Damage", "增加攻擊傷害", "增加攻击伤害"]),
        new("DotMultiplier", "持续伤害加成", ["Damage over Time Multiplier", "持續傷害加成", "持续伤害加成"]),
        new("SkillLevels", "所有技能等级", ["to Level of all Skills", "所有技能等級", "所有技能等级"]),
        new("CriticalChance", "暴击率", ["Critical Hit Chance", "暴擊率", "暴击率"]),
        new("CriticalDamage", "暴击伤害加成", ["Critical Damage Bonus", "暴擊傷害加成", "暴击伤害加成"]),
        new("Accuracy", "命中值", ["to Accuracy Rating", "命中值"]),

        // ---- 速度 / 其他 ----
        new("MoveSpeed", "移动速度", ["Movement Speed", "移動速度", "移动速度"]),
        new("AttackSpeed", "攻击速度", ["Attack Speed", "攻擊速度", "攻击速度"]),
        new("CastSpeed", "施法速度", ["Cast Speed", "施法速度"]),
        new("Rarity", "物品稀有度", ["Rarity of Items", "物品稀有度"]),
    ];

    /// <summary>
    /// 各部位推荐的词缀（顺序即展示顺序）。
    /// 搬砖号有"专堆某个技能"之类特殊取向时，用设置页里的「词缀搜索」加任意词缀。
    /// </summary>
    public static readonly Dictionary<string, string[]> BySlot = new()
    {
        [SlotKeys.Weapon] =
        [
            "AddedPhysicalDamage", "AddedFireDamage", "AddedColdDamage", "AddedLightningDamage",
            "IncreasedPhysicalDamage", "IncreasedElementalDamage", "IncreasedSpellDamage",
            "AttackSpeed", "CastSpeed", "CriticalChance", "CriticalDamage", "SkillLevels", "Accuracy",
        ],
        [SlotKeys.Offhand] =
        [
            "Life", "EnergyShield", "Spirit", "BlockChance",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes",
            "IncreasedSpellDamage", "CastSpeed", "CriticalChance",
        ],
        [SlotKeys.Helmet] =
        [
            "Life", "EnergyShield", "IncreasedEnergyShield", "Spirit", "Rarity",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes", "AllAttributes",
        ],
        [SlotKeys.BodyArmour] =
        [
            "Life", "EnergyShield", "IncreasedEnergyShield", "IncreasedArmour", "Armour", "Evasion", "Strength",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes",
        ],
        [SlotKeys.Gloves] =
        [
            "Life", "EnergyShield", "AttackSpeed", "CastSpeed", "Accuracy",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes", "Strength", "Dexterity",
        ],
        [SlotKeys.Boots] =
        [
            "MoveSpeed", "Life", "EnergyShield", "IncreasedEnergyShield",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes",
        ],
        [SlotKeys.Amulet] =
        [
            "Life", "EnergyShield", "IncreasedEnergyShield", "Mana", "Spirit",
            "AllAttributes", "AllEleRes", "ChaosRes", "CriticalDamage", "Rarity", "SkillLevels",
        ],
        [SlotKeys.Ring1] =
        [
            "Life", "Mana", "AllAttributes", "Accuracy", "Rarity",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes", "AllEleRes",
        ],
        [SlotKeys.Ring2] =
        [
            "Life", "Mana", "AllAttributes", "Accuracy", "Rarity",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes", "AllEleRes",
        ],
        [SlotKeys.Belt] =
        [
            "Life", "EnergyShield", "IncreasedEnergyShield", "IncreasedArmour", "Strength", "LifeRegen",
            "FireRes", "ColdRes", "LightningRes", "ChaosRes",
        ],
        // 药剂 / 咒符 / 珠宝：这三个部位走的是「回复量 / 充能 / 持续时间」这类词缀，
        // 先给能通用的那条（生命 / 魔力 / 抗性 / 属性），专用词缀用设置页的词缀搜索加。
        // ⚠ 这只是「推荐词缀」清单，与词缀池无关（池按 CoE 底材 id 取，见 AffixPoolTags.ResolveBase）。
        [SlotKeys.Flask1] =
        [
            "Life", "LifeRegen", "Mana", "ManaRegen", "StunThreshold",
        ],
        [SlotKeys.Flask2] =
        [
            "Mana", "ManaRegen", "LifeRegen", "StunThreshold",
        ],
        [SlotKeys.Charm1] =
        [
            "Life", "Mana", "FireRes", "ColdRes", "LightningRes", "ChaosRes", "StunThreshold",
        ],
        [SlotKeys.Charm2] =
        [
            "Life", "Mana", "FireRes", "ColdRes", "LightningRes", "ChaosRes", "StunThreshold",
        ],
        [SlotKeys.Charm3] =
        [
            "Life", "Mana", "FireRes", "ColdRes", "LightningRes", "ChaosRes", "StunThreshold",
        ],
        [SlotKeys.Jewel] =
        [
            "Life", "EnergyShield", "Mana", "FireRes", "ColdRes", "LightningRes", "ChaosRes",
            "AllAttributes", "Strength", "Dexterity", "Intelligence",
        ],
    };

    /// <summary>角色总目标用的推荐词缀。</summary>
    public static readonly string[] ForCharacter =
    [
        "Life", "EnergyShield", "Mana", "Spirit",
        "FireRes", "ColdRes", "LightningRes", "ChaosRes", "AllEleRes",
        "Strength", "Dexterity", "Intelligence", "AllAttributes",
        "IncreasedElementalDamage", "IncreasedPhysicalDamage", "DotMultiplier",
        "CriticalChance", "CriticalDamage", "MoveSpeed", "Rarity",
    ];

    public static Preset? Find(string? key) => All.FirstOrDefault(x => x.Key == key);

    /// <summary>取某部位推荐的预设（按 BySlot 顺序）。</summary>
    public static List<Preset> ForSlot(string slotKey)
    {
        if (!BySlot.TryGetValue(slotKey, out var keys))
        {
            return All;
        }

        return [.. keys.Select(Find).Where(x => x != null).Select(x => x!)];
    }

    public static List<Preset> ForCharacterTargets() =>
        [.. ForCharacter.Select(Find).Where(x => x != null).Select(x => x!)];
}
