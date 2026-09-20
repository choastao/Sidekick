using System.Reflection;
using System.Text.Json;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 导入 BD 时的逐字段拷贝（<see cref="ImportedTemplateMapper"/>）的回归测试。
///
/// 背景：这段拷贝原先写在设置页的 .razor 里，手抄字段**漏过三次**
/// （EquippedSource / ImportedTargetsOptional / PobXml）。测试一直测的是 importer 层，
/// 测不到写在 .razor 里的那段拷贝，所以每次都是「测试全绿、功能全废」。
/// 最近一次漏 PobXml 的后果：任何经界面导入的模板都没有 BD 源码 →
/// 引擎试穿 / 备选篮排序 / 逐条收益全部走 NoBuildXml，而提示还叫用户「重新导入一次」。
///
/// 这里用**反射**把「模板的全部可写属性」逐个填上不同的值再比对：
/// 以后给 <see cref="BuildTargetTemplate"/> 加字段而忘了在 mapper 里抄一行，第一个测试就会红。
/// </summary>
public class ImportedTemplateMapperTests
{
    /// <summary>
    /// 有意**不**搬的两个字段：Id（落盘模板自带身份）、UpdatedAt（由 Store.Save 负责）。
    /// 改这个清单必须同时改 <see cref="ImportedTemplateMapper"/> 的文档注释。
    /// </summary>
    private static readonly string[] IntentionallyNotCopied = ["Id", "UpdatedAt"];

    private static readonly PropertyInfo[] CopiedProperties = typeof(BuildTargetTemplate)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(x => x is { CanRead: true, CanWrite: true })
        .Where(x => !IntentionallyNotCopied.Contains(x.Name))
        .ToArray();

    [Fact]
    public void Apply_覆盖模板的全部可写属性()
    {
        var source = new BuildTargetTemplate();
        foreach (var property in CopiedProperties)
        {
            property.SetValue(source, SampleValue(property));
        }

        var target = new BuildTargetTemplate();
        ImportedTemplateMapper.Apply(target, source);

        var missed = CopiedProperties
            .Where(x => !Same(x.GetValue(source), x.GetValue(target)))
            .Select(x => x.Name)
            .ToList();

        Assert.Empty(missed);
    }

    [Fact]
    public void Apply_不清空落盘模板自己的身份()
    {
        var source = new BuildTargetTemplate();
        foreach (var property in CopiedProperties)
        {
            property.SetValue(source, SampleValue(property));
        }

        var target = new BuildTargetTemplate();
        var id = target.Id;
        var updatedAt = target.UpdatedAt;

        ImportedTemplateMapper.Apply(target, source);

        Assert.Equal(id, target.Id);
        Assert.Equal(updatedAt, target.UpdatedAt);
    }

    /// <summary>
    /// 指着 2026-09-20 那次事故的断言（第三轮审计阻断项）：
    /// 漏了它 = C1/C2a/C2b/C2c 对**所有经界面导入的模板**全不可达。
    /// </summary>
    [Fact]
    public void Apply_必须把_BD_源码搬过去()
    {
        var source = new BuildTargetTemplate
        {
            PobXml = "<PathOfBuilding><Build level=\"99\"/></PathOfBuilding>",
        };

        var target = new BuildTargetTemplate();
        ImportedTemplateMapper.Apply(target, source);

        Assert.Equal(source.PobXml, target.PobXml);
    }

    /// <summary>
    /// 反射的测试数据：每种字段类型给一个**非默认、且与其它字段不同**的值。
    /// 模板新增了这里没有的类型时**直接抛错**（比默默不测要好）—— 届时在这里补一条。
    /// </summary>
    private static object? SampleValue(PropertyInfo property)
    {
        var type = property.PropertyType;
        var mark = "SRC:" + property.Name;

        if (type == typeof(string))
        {
            return mark;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(List<ModTarget>))
        {
            return new List<ModTarget> { new() { Label = mark } };
        }

        if (type == typeof(Dictionary<string, List<ModTarget>>))
        {
            return new Dictionary<string, List<ModTarget>> { ["helmet"] = [new ModTarget { Label = mark }] };
        }

        if (type == typeof(Dictionary<string, string>))
        {
            return new Dictionary<string, string> { ["helmet"] = mark };
        }

        if (type == typeof(Dictionary<string, Dictionary<string, double>>))
        {
            return new Dictionary<string, Dictionary<string, double>> { ["helmet"] = new() { ["+90 maximum Life"] = 90 } };
        }

        throw new InvalidOperationException(
            $"BuildTargetTemplate 多了测试没覆盖的字段类型 {type}（{property.Name}）—— 请在 SampleValue 里补一条，" +
            "并确认 ImportedTemplateMapper 会搬它。");
    }

    private static bool Same(object? left, object? right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
