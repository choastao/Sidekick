using Sidekick.Modules.BuildTarget.Models;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// helper 协议层的往返与容错。这些断言的价值在于：引擎那边的字段名以后改了，
/// 解析层不会突然抛异常炸掉整个悬浮窗 —— 只会「取不到这个值」。
/// </summary>
public class PobEngineProtocolTests
{
    [Fact]
    public void Request_encodes_with_id_and_method()
    {
        var json = PobEngineProtocol.Encode(new PobEngineRequest
        {
            Id = 7,
            Method = PobEngineProtocol.MethodStats,
        });

        Assert.Contains("\"id\":7", json);
        Assert.Contains("\"method\":\"stats\"", json);
        // 没有参数时不该出现 params 键（DefaultIgnoreCondition = WhenWritingNull）
        Assert.DoesNotContain("params", json);
    }

    [Fact]
    public void Response_roundtrips_result_values()
    {
        var response = PobEngineProtocol.Decode(
            """{"id":3,"ok":true,"result":{"dps":123456.5,"ehp":9876,"label":"基准","flag":true,"none":null}}""");

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.Equal(123456.5, response.GetDouble("dps"));
        Assert.Equal(9876, response.GetDouble("ehp"));
        Assert.Equal("基准", response.GetString("label"));
        Assert.True(response.GetBool("flag"));
        Assert.Null(response.GetDouble("none"));
        Assert.Null(response.GetDouble("missing"));
        Assert.Null(response.GetString("dps"));
    }

    [Fact]
    public void Error_response_keeps_reason()
    {
        var response = PobEngineProtocol.Decode("""{"id":4,"ok":false,"error":"lua: bad file"}""");

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Equal("lua: bad file", response.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]          // 引擎往 stdout 打了别的东西
    [InlineData("[1,2,3]")]                   // 是 JSON 但不是对象
    public void Garbage_lines_return_null_instead_of_throwing(string? line)
    {
        // 关键：不抛。helper 崩了/输出噪音时，调用方要能继续处理（把它当"这一行无效"）。
        var response = PobEngineProtocol.Decode(line);

        if (line is "[1,2,3]")
        {
            // 数组解成对象会失败 → 同样回 null；这里允许两种实现（null 或空对象），
            // 但不能抛异常。
            Assert.True(response == null || response.Result == null);
            return;
        }

        Assert.Null(response);
    }
}
