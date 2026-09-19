namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>评估结论。新手只看这个颜色 + 一句话。</summary>
public enum Verdict
{
    /// <summary>信息不足（没建模板 / 没采集当前装备）。</summary>
    Unknown = 0,

    /// <summary>建议换上：硬性门槛全过，且对标当前装备有提升。</summary>
    Good,

    /// <summary>可留观：硬性门槛过了但有短板，或与当前装备互有来回。</summary>
    Warn,

    /// <summary>不建议：硬性门槛未过。</summary>
    Bad,
}

/// <summary>单条词缀的检查结果。</summary>
public class ModCheck
{
    public string Label { get; set; } = "";

    /// <summary>目标下限。</summary>
    public double MinValue { get; set; }

    public bool Required { get; set; } = true;

    /// <summary>新装备上该词缀的取值；没找到为 null。</summary>
    public double? New { get; set; }

    /// <summary>当前装备上该词缀的取值；没采集/没找到为 null。</summary>
    public double? Current { get; set; }

    /// <summary>是否达标。</summary>
    public bool Pass => New.HasValue && New.Value >= MinValue;

    /// <summary>相对当前装备的增减；无法比较时为 null。</summary>
    public double? Delta => New.HasValue && Current.HasValue ? New - Current : null;

    /// <summary>命中到的原始词缀行（方便用户核对匹配对不对）。</summary>
    public string? MatchedLine { get; set; }

    /// <summary>
    /// 该目标词缀的匹配关键词（原样带过来）。改造面板用它去词缀类型表里查前缀/后缀，
    /// 不额外重写一套判定逻辑。
    /// </summary>
    public List<string> Match { get; set; } = [];
}

/// <summary>单个部位的评估结果。</summary>
public class SlotEvaluation
{
    public string SlotKey { get; set; } = "";

    /// <summary>部位显示名。</summary>
    public string SlotLabel { get; set; } = "";

    public List<ModCheck> Checks { get; set; } = [];

    public Verdict Verdict { get; set; } = Verdict.Unknown;

    /// <summary>一句话结论。</summary>
    public string Headline { get; set; } = "";

    /// <summary>当前装备快照的名称（若已采集）。</summary>
    public string? EquippedName { get; set; }

    /// <summary>该部位是否已采集当前装备快照。</summary>
    public bool HasEquipped { get; set; }

    /// <summary>该部位是否存有快照文本（但可能解析失败）。</summary>
    public bool HasSnapshot { get; set; }

    /// <summary>存了快照却解析不出来（通常是物品语言设置与游戏客户端不一致）。</summary>
    public bool EquippedUnparsable => HasSnapshot && !HasEquipped;
}

/// <summary>一次完整评估的输出。</summary>
public class BuildTargetResult
{
    public bool HasTemplate { get; set; }

    public string? NewItemName { get; set; }

    /// <summary>各候选部位的评估（戒指会有两条）。</summary>
    public List<SlotEvaluation> Slots { get; set; } = [];

    /// <summary>角色级合计估算。</summary>
    public List<CharacterEstimate> Character { get; set; } = [];

    public Verdict Verdict { get; set; } = Verdict.Unknown;

    /// <summary>给新手看的一句话结论。</summary>
    public string Headline { get; set; } = "";
}

/// <summary>角色级合计的估算（只含装备上的加法类词缀，不含天赋/被动）。</summary>
public class CharacterEstimate
{
    public string Label { get; set; } = "";

    public double MinValue { get; set; }

    /// <summary>当前全身（已采集快照的合计）。</summary>
    public double? CurrentSum { get; set; }

    /// <summary>换上新装备后的合计。</summary>
    public double? NewSum { get; set; }

    /// <summary>已采集几个部位（用于说明估算可信度）。</summary>
    public int CollectedSlots { get; set; }

    public bool Pass => NewSum.HasValue && NewSum.Value >= MinValue;

    public double? Delta => NewSum.HasValue && CurrentSum.HasValue ? NewSum - CurrentSum : null;
}
