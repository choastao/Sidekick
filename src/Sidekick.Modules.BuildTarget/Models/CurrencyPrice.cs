namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 内置通货价格表（currency-prices.json）的根节点。文件随分发包发布，离线可用。
/// </summary>
public class CurrencyPriceFile
{
    /// <summary>赛季名。</summary>
    public string League { get; set; } = "";

    /// <summary>抓取时间（ISO 8601，带时区偏移）。</summary>
    public DateTimeOffset? FetchedAt { get; set; }

    /// <summary>数据来源说明。</summary>
    public string Source { get; set; } = "";

    /// <summary>主计价单位 id（divine）。</summary>
    public string Primary { get; set; } = "divine";

    /// <summary>副计价单位 id（chaos）。</summary>
    public string Secondary { get; set; } = "chaos";

    /// <summary>1 个主计价单位（divine）= rate 个副计价单位（chaos）。</summary>
    public double Rate { get; set; }

    /// <summary>类目表：Currency / Essences / Runes 等。</summary>
    public Dictionary<string, CurrencyPriceCategory> Categories { get; set; } = [];
}

/// <summary>一个类目（例如 Currency）。</summary>
public class CurrencyPriceCategory
{
    /// <summary>文件里声明的条数。</summary>
    public int Count { get; set; }

    public List<CurrencyPriceLine> Lines { get; set; } = [];
}

/// <summary>一条通货价格。</summary>
public class CurrencyPriceLine
{
    /// <summary>稳定 id，例如 chaos / divine。</summary>
    public string Id { get; set; } = "";

    /// <summary>游戏官方名（繁体，来自物品库，原样显示）。</summary>
    public string Name { get; set; } = "";

    /// <summary>英文名，用于搜索。</summary>
    public string NameEn { get; set; } = "";

    /// <summary>单价，单位为主计价通货（divine）。</summary>
    public double Primary { get; set; }

    /// <summary>单价，单位为副计价通货（chaos）。</summary>
    public double Secondary { get; set; }

    /// <summary>成交量，用于排序。</summary>
    public double Volume { get; set; }

    /// <summary>7 天涨跌幅（%）。</summary>
    public double Change7d { get; set; }

    /// <summary>所属类目，加载时由服务填上，界面上用于搜索提示。</summary>
    public string Category { get; set; } = "";
}

/// <summary>
/// 价格表的加载结果：要么拿到数据，要么带着「为什么没有」的原因，绝不静默失败。
/// </summary>
public class CurrencyPriceSnapshot
{
    /// <summary>加载成功时的数据；失败为 null。</summary>
    public CurrencyPriceFile? Data { get; init; }

    /// <summary>实际读的是哪个文件。</summary>
    public string? LoadedFrom { get; init; }

    /// <summary>文件存在但读不出来时的原因。</summary>
    public string? LoadError { get; init; }

    /// <summary>按顺序找过的位置（界面上如实展示，告诉用户该放哪里）。</summary>
    public IReadOnlyList<string> SearchedPaths { get; init; } = [];

    public bool HasData => Data != null;
}
