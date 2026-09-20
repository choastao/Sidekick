using System.Text.Json;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 「引擎自己不支持的词缀」这条透传链的解析端（helper → 主程序）。
///
/// 为什么要它：这类词缀进算式时是 0，而界面上的 0 会被用户读成「这条词缀没贡献」——
/// 结论就错了（「火抗在这件装备上不值钱」）。helper 侧从 PoB 的 `modLine.extra` 读出来透传
/// （见 Engine/tao-engine-server.lua），这里钉住「主程序正确读懂它」。
/// </summary>
public class EngineUnsupportedTests
{
    private static PobEngineResponse Decode(string line) =>
        PobEngineProtocol.Decode(line) ?? throw new InvalidOperationException("decode 返回 null");

    [Fact]
    public void 读出引擎不支持的词缀行()
    {
        var response = Decode("""
            {"id":1,"ok":true,"result":{"stats":{"dps":1,"ehp":2,"life":3},
             "unsupported":{"count":2,"lines":["+1234 to absolutely nothing","Weird Mod Line"]}}}
            """);

        var lines = PobCompareService.ReadEngineUnsupported(response);

        Assert.Equal(2, lines.Count);
        Assert.Contains("+1234 to absolutely nothing", lines);
        Assert.Contains("Weird Mod Line", lines);
    }

    [Fact]
    public void 没有这一项时是空的_不是猜出来的数字()
    {
        // 引擎老版本 / 完全支持的装备 —— 都必须当「没有盲区」，而不是「读不出来」
        var response = Decode("""{"id":1,"ok":true,"result":{"stats":{"dps":1,"ehp":2,"life":3}}}""");

        Assert.Empty(PobCompareService.ReadEngineUnsupported(response));
    }

    [Fact]
    public void 形状不对时也不抛_当空处理()
    {
        // lines 不是数组、或元素不是字符串：不抛异常，也不编内容
        var response = Decode("""{"id":1,"ok":true,"result":{"unsupported":{"count":3,"lines":"oops"}}}""");
        Assert.Empty(PobCompareService.ReadEngineUnsupported(response));

        var mixed = Decode("""{"id":1,"ok":true,"result":{"unsupported":{"lines":["ok line",42,null,""]}}}""");
        Assert.Equal(new[] { "ok line" }, PobCompareService.ReadEngineUnsupported(mixed));
    }

    [Fact]
    public void 结果对象把两个数分开带出来()
    {
        // 「我们转换层的缺口」与「引擎自己的盲区」是两个数，别混成一个（旧文案混过）
        var result = PobCompareResult.Ok(
            new PobStats(1, 1, 1),
            new PobStats(2, 2, 2),
            unmappedAffixes: 3,
            engineUnsupported: ["+1234 to absolutely nothing"]);

        Assert.Equal(3, result.UnmappedAffixes);
        Assert.Equal(1, result.EngineUnsupportedCount);
        Assert.True(result.HasDelta);
    }
}
