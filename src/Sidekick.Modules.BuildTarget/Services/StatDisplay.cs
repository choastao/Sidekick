namespace Sidekick.Modules.BuildTarget.Services;

using Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「换装后」这一列的显示决策。抽出来是为了能单独测试——
/// 着色逻辑留在 Razor 组件里的话，删掉它不会有任何测试变红（CC 审计发现）。
/// </summary>
public static class StatDisplay
{
    /// <summary>这件装备不提供该条属性时用长破折号，和「取到了但为 0」区分开。</summary>
    public const string NotProvided = "—";

    public static string Value(ModCheck check) =>
        check.Provided ? check.New!.Value.ToString("0.##") : NotProvided;

    public static string Class(ModCheck check)
    {
        if (!check.Provided)
        {
            // 硬性要求 + 这件装备根本没有这条 = 未达标，必须标红。
            // 否则会出现「同一屏上判了不建议、这一格却是灰的」自相矛盾（CC 审计发现）。
            return check.Required ? "text-red-400" : "text-stone-500";
        }

        if (check.Pass)
        {
            return "";
        }

        // 只有「用户自己勾的硬性目标」没达到才标红；参考值没到用琥珀色提示。
        return check.Required ? "text-red-400" : "text-amber-400";
    }
}
