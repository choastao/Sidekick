namespace Sidekick.Modules.BuildTarget.Services;

using Sidekick.Game.Parser.Items;

/// <summary>
/// 判断「正在看的这件」是不是某部位已存的快照（就是用户身上那件）。
///
/// 为什么不能直接比较字符串：存下来的快照是**用户粘贴的原始文本**，
/// 而被评估的物品文本走的是 <see cref="OriginalText"/>——它内部已经做过
/// `CleanString` + `RemoveSquareBrackets`。原样对比几乎永远不相等（CC 审计发现）。
/// 所以两边都要过一遍同样的清洗，再按行归一化后比较。
///
/// 保守原则：只有确实一致才认为「就是当前装备」。同一件独特装的不同 roll
/// 数值不同，必须判为不同（否则会告诉用户「这就是你现在穿的」，而其实是另一件）。
/// </summary>
public static class ItemTextComparer
{
    public static bool AreSame(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return Key(a) == Key(b);
    }

    /// <summary>清洗 + 按行归一化：统一换行、去掉空行与每行首尾空白。</summary>
    private static string Key(string text)
    {
        var cleaned = new OriginalText(text).Text;
        return string.Join(
            "\n",
            cleaned.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));
    }
}
