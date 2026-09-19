using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;
using Sidekick.Modules.BuildTarget.Localization;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 从真实的 resx 取文案（缺键会直接暴露出来，正好当资源检查用）。
/// 中英两份资源都要能取到，所以语言可切换。
/// </summary>
internal sealed class TestLocalizer : IStringLocalizer<BuildTargetResources>
{
    private readonly ResourceManager manager = new(
        "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
        typeof(BuildTargetResources).Assembly);

    private readonly CultureInfo culture;

    public TestLocalizer(CultureInfo? culture = null) =>
        this.culture = culture ?? CultureInfo.GetCultureInfo("zh");

    public LocalizedString this[string name]
    {
        get
        {
            var value = manager.GetString(name, culture);
            return new LocalizedString(name, value ?? name, value == null);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var value = manager.GetString(name, culture);
            return new LocalizedString(
                name,
                value == null ? name : string.Format(culture, value, arguments),
                value == null);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
