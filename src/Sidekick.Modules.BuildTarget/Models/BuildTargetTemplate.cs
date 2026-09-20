namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 一条可评估的词缀目标：在物品文本里按关键词找到它，取值后与 <see cref="MinValue"/> 比较。
/// </summary>
public class ModTarget
{
    /// <summary>稳定标识，用于 UI 里增删改。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>显示名，例如「最大生命」。</summary>
    public string Label { get; set; } = "";

    /// <summary>匹配关键词，任一命中即算该词缀（对物品词缀行做包含匹配，忽略大小写）。</summary>
    public List<string> Match { get; set; } = [];

    /// <summary>目标最低值。角色级=全身合计下限；部位级=单件下限。</summary>
    public double MinValue { get; set; }

    /// <summary>true=硬性门槛（不达标即红）；false=加分项（只显示不拦）。</summary>
    public bool Required { get; set; }
}

/// <summary>
/// 一套目标 BD 模板：角色级目标 + 各部位门槛 + 当前装备快照。
/// </summary>
public class BuildTargetTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>用户自定义的模板名。</summary>
    public string Name { get; set; } = "";

    /// <summary>角色级目标：对整个角色求和后比较（生命 / 护盾 / 抗性等加法类词缀）。</summary>
    public List<ModTarget> Character { get; set; } = [];

    /// <summary>部位级门槛：key 见 <see cref="SlotKeys"/>，value 是该部位的词缀目标。</summary>
    public Dictionary<string, List<ModTarget>> Slots { get; set; } = [];

    /// <summary>
    /// 当前装备快照：部位 key -&gt; 该部位装备的原始物品文本。
    /// 由用户在游戏里对身上的装备按一次 Ctrl+D 采集（Sidekick 读不到已装备物品，只能这样拿基准）。
    /// </summary>
    public Dictionary<string, string> Equipped { get; set; } = [];

    /// <summary>
    /// 导入 BD 时抽好的部位数值：部位 key -&gt; (词缀名 -&gt; 数值)。
    ///
    /// 导入的 BD 物品文本是 PoB 自己的英文格式，不走游戏解析器（解析器按客户端语言解析，
    /// 对不上 PoB 文本），所以在导入时就把数值抽出来存下，评估时直接查表。
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> EquippedStats { get; set; } = [];

    /// <summary>导入 BD 时各部位的装备名（界面上「对比对象」显示用）。</summary>
    public Dictionary<string, string> EquippedNames { get; set; } = [];

    private Dictionary<string, string> equippedSource = [];

    /// <summary>
    /// 各部位基准的来源：bd = 导入 BD 自带，manual = 游戏内悬停采集。
    /// 老配置里没有这个字段：读出来是空字典（反序列化到 null 也当空字典用），界面上按「来源未知」显示。
    /// </summary>
    public Dictionary<string, string> EquippedSource
    {
        get => equippedSource ??= [];
        set => equippedSource = value ?? [];
    }

    /// <summary>来源：手动建立，或导入的 BD 标识。</summary>
    public string? ImportedFrom { get; set; }

    /// <summary>
    /// 导入时的 BD 源码（PoB 的 XML 原文）。PoB 引擎试穿要拿它当基准，所以导入时就存下来。
    /// **不能靠 <see cref="ImportedFrom"/> 反推**：那个字段是截断过的展示用字符串
    /// （而且链接形态还要联网再取一次）。老模板没有这个字段时，试穿功能按「请重新导入一次 BD」提示。
    /// </summary>
    public string? PobXml { get; set; }

    /// <summary>
    /// 「从 BD 导入的部位门槛已改成参考值」的迁移标记。
    ///
    /// 早期版本把导入时那件装备的数值直接写成了该部位的**硬性门槛**（Required=true），
    /// 结果任何属性不同的装备都被判「不建议」——用户实测：把自己正穿着的头盔标为
    /// 当前装备后，照样被判「不建议装备」。现在导入的门槛一律是参考值（Required=false），
    /// 硬性标准只留给用户自己勾。这个标记用于对老模板做一次性迁移。
    /// </summary>
    public bool ImportedTargetsOptional { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 模板文件（落盘结构）。
/// </summary>
public class BuildTargetFile
{
    public int Version { get; set; } = 1;

    public string? ActiveId { get; set; }

    public List<BuildTargetTemplate> Templates { get; set; } = [];
}
