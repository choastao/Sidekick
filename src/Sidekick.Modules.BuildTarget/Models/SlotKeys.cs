using Sidekick.Game.ItemClasses;
using Sidekick.Game.Parser.Items;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 部位槽定义与「物品 → 部位」映射。
///
/// 药剂 / 咒符 / 珠宝（v3.7 新增）的口径 —— 按 PoB2 的权威形态定，不是「各算一个槽」：
///   · <b>药剂 2 槽</b>：PoE2 的两个药剂槽就是「生命 / 魔力」，PoB 的物品集里也正好是
///     `Flask 1` / `Flask 2` 两行 → 生命药剂落 flask1、魔力药剂落 flask2，槽键与 PoB 槽名 1:1。
///     若按「药剂算一个槽」（原默认方案），一件生命药剂和一件魔力药剂会解析到**同一个键**，
///     导入 BD 时后一件把前一件**静默覆盖**（基准快照丢一件），正是本项目踩过的静默数据丢失坑。
///   · <b>咒符 3 槽</b>：PoB 的 `Charm 1/2/3`（本 BD 三条全在用；游戏里槽数由腰带决定，
///     没装的那几槽留空即可，与 PoB 一致）。新咒符可进任意一槽，所以解析返回三槽候选
///     （与戒指两槽同一套做法：由评估器挑第一个有基准的槽）。
///   · <b>珠宝 1 槽（聚合）</b>：珠宝在 PoB 里**不是槽位**，是天赋树上的 `SocketIdURL nodeId`
///     （本 BD 有 4 个孔，孔数随天赋树变）。固定槽数无从谈起，只能聚合成一个
///     「任一珠宝孔」语义的槽 —— 这是唯一不会说谎的做法，界面按此注明。
/// </summary>
public static class SlotKeys
{
    /// <summary>部位判断不出来时用这个（物品类别缺失 / 不是装备）。</summary>
    public const string Unknown = "unknown";

    public const string Weapon = "weapon";
    public const string Offhand = "offhand";
    public const string Helmet = "helmet";
    public const string BodyArmour = "bodyarmour";
    public const string Gloves = "gloves";
    public const string Boots = "boots";
    public const string Amulet = "amulet";
    public const string Ring1 = "ring1";
    public const string Ring2 = "ring2";
    public const string Belt = "belt";

    /// <summary>生命药剂槽（PoB 槽名 <c>Flask 1</c>）。</summary>
    public const string Flask1 = "flask1";

    /// <summary>魔力药剂槽（PoB 槽名 <c>Flask 2</c>）。</summary>
    public const string Flask2 = "flask2";

    public const string Charm1 = "charm1";
    public const string Charm2 = "charm2";
    public const string Charm3 = "charm3";

    /// <summary>珠宝槽（聚合：珠宝在 PoB 里是天赋树上的镶嵌孔，不是槽位）。</summary>
    public const string Jewel = "jewel";

    /// <summary>固定顺序，UI 按此展示。</summary>
    public static readonly string[] All =
    [
        Weapon, Offhand, Helmet, BodyArmour, Gloves, Boots, Amulet, Ring1, Ring2, Belt,
        Flask1, Flask2, Charm1, Charm2, Charm3, Jewel,
    ];

    private static readonly Dictionary<string, ItemClass[]> Direct = new()
    {
        [Helmet] = [ItemClass.Helmet],
        [BodyArmour] = [ItemClass.BodyArmour],
        [Gloves] = [ItemClass.Gloves],
        [Boots] = [ItemClass.Boots],
        [Amulet] = [ItemClass.Amulet],
        [Belt] = [ItemClass.Belt],
        [Offhand] = [ItemClass.Shield, ItemClass.Focus, ItemClass.Buckler, ItemClass.Quiver],
    };

    /// <summary>
    /// 这件物品可能落在哪些部位槽。戒指有两槽、咒符有三槽，都返回，由 UI 分别对比。
    /// 无法判断部位时返回空数组。
    /// </summary>
    public static string[] ResolveFor(Item? item)
    {
        if (item?.ItemClass == null)
        {
            return [];
        }

        var type = item.ItemClass.Type;
        if (type == ItemClass.Unknown)
        {
            return [];
        }

        if (type == ItemClass.Ring)
        {
            return [Ring1, Ring2];
        }

        // PoE2 的两个药剂槽按「生命 / 魔力」定，不按摆放顺序猜：
        // 游戏里这两个槽本身就是生命药剂槽与魔力药剂槽。
        if (type == ItemClass.LifeFlask)
        {
            return [Flask1];
        }

        if (type == ItemClass.ManaFlask)
        {
            return [Flask2];
        }

        if (type == ItemClass.Charms)
        {
            return [Charm1, Charm2, Charm3];
        }

        if (type == ItemClass.Jewel)
        {
            return [Jewel];
        }

        if (item.ItemClass.IsWeapon())
        {
            return [Weapon];
        }

        foreach (var pair in Direct)
        {
            if (pair.Value.Contains(type))
            {
                return [pair.Key];
            }
        }

        return [];
    }
}
