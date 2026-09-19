namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 一件（或一批）装备能提供的抗性 / 属性加值。
///
/// 这里只装「加法类」词缀的直和：+X% 抗性、+X 属性。
/// 天赋树、升华、任务奖励、药剂，以及「提高抗性」这类乘法词缀都不在这里 —— 那些算不准，不如不算。
/// </summary>
public class BasketContribution
{
    public double FireResistance { get; set; }

    public double ColdResistance { get; set; }

    public double LightningResistance { get; set; }

    public double ChaosResistance { get; set; }

    public double Strength { get; set; }

    public double Dexterity { get; set; }

    public double Intelligence { get; set; }

    public double Get(BasketStat stat) => stat switch
    {
        BasketStat.FireResistance => FireResistance,
        BasketStat.ColdResistance => ColdResistance,
        BasketStat.LightningResistance => LightningResistance,
        BasketStat.ChaosResistance => ChaosResistance,
        BasketStat.Strength => Strength,
        BasketStat.Dexterity => Dexterity,
        BasketStat.Intelligence => Intelligence,
        _ => 0,
    };

    public void Set(BasketStat stat, double value)
    {
        switch (stat)
        {
            case BasketStat.FireResistance:
                FireResistance = value;
                break;
            case BasketStat.ColdResistance:
                ColdResistance = value;
                break;
            case BasketStat.LightningResistance:
                LightningResistance = value;
                break;
            case BasketStat.ChaosResistance:
                ChaosResistance = value;
                break;
            case BasketStat.Strength:
                Strength = value;
                break;
            case BasketStat.Dexterity:
                Dexterity = value;
                break;
            case BasketStat.Intelligence:
                Intelligence = value;
                break;
        }
    }

    public void Add(BasketStat stat, double value) => Set(stat, Get(stat) + value);

    public void Add(BasketContribution other)
    {
        foreach (var stat in BasketStatExtensions.All)
        {
            Add(stat, other.Get(stat));
        }
    }

    public bool IsEmpty => BasketStatExtensions.All.All(x => Get(x) == 0);
}

/// <summary>备选篮里统计的指标：四个抗性 + 三条属性。</summary>
public enum BasketStat
{
    FireResistance,
    ColdResistance,
    LightningResistance,
    ChaosResistance,
    Strength,
    Dexterity,
    Intelligence,
}

public static class BasketStatExtensions
{
    /// <summary>界面展示顺序：先抗性，再属性。</summary>
    public static readonly BasketStat[] All =
    [
        BasketStat.FireResistance,
        BasketStat.ColdResistance,
        BasketStat.LightningResistance,
        BasketStat.ChaosResistance,
        BasketStat.Strength,
        BasketStat.Dexterity,
        BasketStat.Intelligence,
    ];

    public static readonly BasketStat[] Resistances =
    [
        BasketStat.FireResistance,
        BasketStat.ColdResistance,
        BasketStat.LightningResistance,
        BasketStat.ChaosResistance,
    ];

    public static readonly BasketStat[] Attributes =
    [
        BasketStat.Strength,
        BasketStat.Dexterity,
        BasketStat.Intelligence,
    ];

    public static bool IsResistance(this BasketStat stat) => Resistances.Contains(stat);

    /// <summary>界面上这个指标的短名（火 / 冰 / 电 / 混 / 力 / 敏 / 智）。</summary>
    public static string ResourceKey(this BasketStat stat) => "Basket_Stat_" + stat;
}
