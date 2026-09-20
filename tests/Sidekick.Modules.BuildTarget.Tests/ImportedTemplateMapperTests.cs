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
///
/// 第四轮审计（§3.2）指出这个守卫自己有三处盲区，都在这里补上了：
///   ① 样本值与默认值相同的字段会漏判 → <c>Apply_样本值必须与默认值不同</c>；
///   ② 只读属性会被 CanWrite 静默排除 → <c>模板不应当有只读属性</c>；
///   ③ 清单意外为空时主断言会空过 → <c>Assert.NotEmpty</c>。
/// </summary>
public class ImportedTemplateMapperTests
{
    /// <summary>
    /// 有意**不**搬的两个字段：Id（落盘模板自带身份）、UpdatedAt（由 Store.Save 负责）。
    /// 改这个清单必须同时改 <see cref="ImportedTemplateMapper"/> 的文档注释。
    /// </summary>
    private static readonly string[] IntentionallyNotCopied = ["Id", "UpdatedAt"];

    private static readonly PropertyInfo[] AllProperties = typeof(BuildTargetTemplate)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(x => x.CanRead)
        .ToArray();

    private static readonly PropertyInfo[] CopiedProperties = AllProperties
        .Where(x => x.CanWrite)
        .Where(x => !IntentionallyNotCopied.Contains(x.Name))
        .ToArray();

    /// <summary>清单意外变空（或字段被误过滤掉）时，下面的循环会一个字段都不查却「全过」——先钉住这一点。</summary>
    [Fact]
    public void 守卫清单本身不为空()
    {
        Assert.NotEmpty(CopiedProperties);
        Assert.Equal(10, CopiedProperties.Length);
    }

    /// <summary>
    /// 只读属性既进不了 <see cref="CopiedProperties"/>、也进不了「有意不搬」清单，等于无人看守 ——
    /// 一旦出现就直接红，逼人来看一眼「这个属性要不要搬」。
    /// </summary>
    [Fact]
    public void 模板不应当有只读属性()
    {
        var readOnly = AllProperties
            .Where(x => !x.CanWrite)
            .Select(x => x.Name)
            .ToList();

        Assert.Empty(readOnly);
    }

    /// <summary>
    /// 样本值必须与「全新实例的默认值」不同：否则漏抄时两边序列化出来一样，主断言会空过。
    /// 典型反例（审计 §3.2 ①）：将来加一个**默认为 true** 的布尔属性，而 <see cref="SampleValue"/> 给 bool 的样本就是 true。
    /// </summary>
    [Fact]
    public void 样本值必须与默认值不同()
    {
        var fresh = new BuildTargetTemplate();

        var sameAsDefault = CopiedProperties
            .Where(x => Same(x.GetValue(fresh), SampleValue(x)))
            .Select(x => x.Name)
            .ToList();

        Assert.Empty(sameAsDefault);
    }

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

    /// <summary>
    /// 身份字段用**写死的值**断言，不依赖时钟（审计 §3.2 末：两个 <c>DateTimeOffset.Now</c> 可能落在同一刻度，
    /// 那样这半条断言就失去效力）。
    /// </summary>
    [Fact]
    public void Apply_不清空落盘模板自己的身份()
    {
        var source = new BuildTargetTemplate();
        foreach (var property in CopiedProperties)
        {
            property.SetValue(source, SampleValue(property));
        }

        var target = new BuildTargetTemplate
        {
            Id = "kept-id",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        ImportedTemplateMapper.Apply(target, source);

        Assert.Equal("kept-id", target.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch, target.UpdatedAt);
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
    /// 模板新增了这里没有的类型时**直接抛错**（比默默不测要好）—— 届时在这里补一条，
    /// 别忘了 <c>样本值必须与默认值不同</c> 会替你检查新的样本值是不是恰好等于默认值。
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
            // bool 只有两个取值，样本固定 true 时「默认值也是 true」的字段会漏判 ——
            // 那由「样本值必须与默认值不同」这条测试兜住（它会红）。
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
