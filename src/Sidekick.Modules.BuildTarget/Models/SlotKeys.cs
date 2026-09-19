using Sidekick.Game.ItemClasses;
using Sidekick.Game.Parser.Items;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 部位槽定义与「物品 → 部位」映射。
/// </summary>
public static class SlotKeys
{
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

    /// <summary>固定顺序，UI 按此展示。</summary>
    public static readonly string[] All =
    [
        Weapon, Offhand, Helmet, BodyArmour, Gloves, Boots, Amulet, Ring1, Ring2, Belt,
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
    /// 这件物品可能落在哪些部位槽。戒指有两槽，都返回，由 UI 分别对比。
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
