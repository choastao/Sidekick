namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 备选篮里的一件装备。
///
/// 落盘的是「算好的数值」而不是物品原文：数值与客户端语言无关，读回来也不用再跑一遍解析器。
/// <see cref="Id"/> 是物品文本的 hash，同一件装备重复加入只会覆盖这一条。
/// </summary>
public class CandidateBasketItem
{
    /// <summary>物品文本的 hash（去重用）。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名：有随机名（稀有 / 传奇）用随机名，否则用基底名。</summary>
    public string Name { get; set; } = "";

    /// <summary>基底名，例如「紫晶戒指」。</summary>
    public string BaseType { get; set; } = "";

    /// <summary>部位，取值见 <see cref="SlotKeys"/>。</summary>
    public string SlotKey { get; set; } = "";

    /// <summary>加入时间。</summary>
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>是否计入合计（勾选状态）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>这件装备在抗性 / 属性上的贡献。</summary>
    public BasketContribution Contribution { get; set; } = new();
}

/// <summary>备选篮的落盘结构。</summary>
public class CandidateBasketFile
{
    public int Version { get; set; } = 1;

    /// <summary>抗性上限，默认 75。带 +最大抗性 的装备 / 升华请自己改。</summary>
    public double ResistanceCap { get; set; } = 75;

    public List<CandidateBasketItem> Items { get; set; } = [];
}
