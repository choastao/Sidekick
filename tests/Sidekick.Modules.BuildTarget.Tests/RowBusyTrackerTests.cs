using Sidekick.Modules.BuildTarget.Models;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 备选篮「哪一行正在算」的记分牌（<see cref="RowBusyTracker"/>）。
///
/// 病根：同一行的两次计算可以重叠（列表重算清空后重新展开、或收起再展开），
/// 老的那次收尾若按行 id 清标记，会把**新那次刚设上的忙碌标记**一起清掉，
/// 界面随即显示「这条算不了」—— 那是一条不成立的结论（它其实还在算）。
/// 因此收尾要交回令牌，只在「这一行还是我这一轮」时才清。
/// </summary>
public class RowBusyTrackerTests
{
    [Fact]
    public void 开始那一行是忙碌的_收尾后不再忙碌()
    {
        var tracker = new RowBusyTracker();

        var token = tracker.Begin("row-1");

        Assert.True(tracker.IsBusy("row-1"));

        tracker.End("row-1", token);

        Assert.False(tracker.IsBusy("row-1"));
    }

    [Fact]
    public void 先结束的那一行_不影响还在算的另一行()
    {
        var tracker = new RowBusyTracker();

        var first = tracker.Begin("row-1");
        var second = tracker.Begin("row-2");

        tracker.End("row-1", first);

        Assert.False(tracker.IsBusy("row-1"));
        Assert.True(tracker.IsBusy("row-2"));

        tracker.End("row-2", second);

        Assert.False(tracker.IsBusy("row-2"));
    }

    /// <summary>核心回归：清空（列表重算 / 换目标 BD）之后重新展开同一行，老调用的收尾不许清掉新的忙碌标记。</summary>
    [Fact]
    public void 清空后重新展开同一行_老调用的收尾不清掉新一轮的忙碌标记()
    {
        var tracker = new RowBusyTracker();

        var stale = tracker.Begin("row-1");
        tracker.Clear();
        tracker.Begin("row-1");

        tracker.End("row-1", stale);

        Assert.True(tracker.IsBusy("row-1"));
    }

    /// <summary>同一条路径的另一种形状：没有清空，直接把同一行重新发起一次（收起再展开）。</summary>
    [Fact]
    public void 同一行被重新发起_只有最新一轮的收尾生效()
    {
        var tracker = new RowBusyTracker();

        var stale = tracker.Begin("row-1");
        var fresh = tracker.Begin("row-1");

        tracker.End("row-1", stale);
        Assert.True(tracker.IsBusy("row-1"));

        tracker.End("row-1", fresh);
        Assert.False(tracker.IsBusy("row-1"));
    }

    [Fact]
    public void 清空之后没有任何一行是忙碌的()
    {
        var tracker = new RowBusyTracker();

        tracker.Begin("row-1");
        tracker.Begin("row-2");

        tracker.Clear();

        Assert.False(tracker.IsBusy("row-1"));
        Assert.False(tracker.IsBusy("row-2"));
    }
}
