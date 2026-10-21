using PingWhen.Features;
using static PingWhen.Features.Conditions;

namespace PingWhen.Tests;

[TestClass]
public class ConditionsTests
{
    /// <summary>Feeds one value per second starting at t=1000 ms; returns the second (1-based) it fired at, or 0.</summary>
    static int RunQuiet(double?[] values, double below = 5, int seconds = 3, bool alreadyRunning = false)
    {
        var s = new QuietState(alreadyRunning, null, 0);
        for (var i = 0; i < values.Length; i++)
        {
            (s, var fired) = Quiet(s, values[i], below, seconds, 1000 + i * 1000L);
            if (fired) return i + 1;
        }
        return 0;
    }

    [TestMethod]
    public void QuietArmsOnlyAfterBeingAbove() => Assert.AreEqual(0, RunQuiet([1, 1, 1, 1, 1, 1]));

    [TestMethod]
    public void QuietFiresAfterNSecondsBelow() => Assert.AreEqual(5, RunQuiet([50, 1, 1, 1, 1, 1])); // below since #2, 3 s later = #5

    [TestMethod]
    public void AlreadyRunningSkipsArming() => Assert.AreEqual(4, RunQuiet([1, 1, 1, 1, 1], alreadyRunning: true));

    [TestMethod]
    public void SpikeRestartsTheTimer() => Assert.AreEqual(8, RunQuiet([50, 1, 1, 50, 1, 1, 1, 1]));

    [TestMethod]
    public void ThresholdIsStrictlyBelow() => Assert.AreEqual(0, RunQuiet([50, 5, 5, 5, 5, 5]));

    [TestMethod]
    public void MissingSampleRestartsTheTimer() => Assert.AreEqual(8, RunQuiet([50, 1, 1, null, 1, 1, 1, 1]));

    [TestMethod]
    public void GapInSamplingRestartsTheTimer()
    {
        var s = new QuietState(true, null, 0);
        (s, _) = Quiet(s, 1, 5, 3, 1000);
        (s, _) = Quiet(s, 1, 5, 3, 2000);
        (s, var fired) = Quiet(s, 1, 5, 3, 60_000); // slept: a minute without samples
        Assert.IsFalse(fired);
        (s, fired) = Quiet(s, 1, 5, 3, 61_000);
        Assert.IsFalse(fired);
        (_, fired) = Quiet(s, 1, 5, 3, 64_000);
        Assert.IsTrue(fired);
    }

    [TestMethod]
    public void ResetAfterSleepDoesNotFireAtOnce()
    {
        var s = new QuietState(true, 1000, 3000) with { BelowSince = null, LastAt = 0 }; // Engine.ResetTimers
        (s, var fired) = Quiet(s, 1, 5, 3, 3500);
        Assert.IsFalse(fired);
        Assert.AreEqual(3500, s.BelowSince);
    }

    static int RunGrowth(long[] sizes, int seconds = 3)
    {
        var s = new GrowthState(sizes[0], false, null, 0);
        for (var i = 1; i < sizes.Length; i++)
        {
            (s, var fired) = Growth(s, sizes[i], seconds, i * 1000L);
            if (fired) return i;
        }
        return 0;
    }

    [TestMethod]
    public void GrowthThenStopFires() => Assert.AreEqual(6, RunGrowth([10, 20, 30, 30, 30, 30, 30]));

    [TestMethod]
    public void NoGrowthSeenNeverFires() => Assert.AreEqual(0, RunGrowth([10, 10, 10, 10, 10, 10, 10]));

    [TestMethod]
    public void ShrinkingRestartsTheTimer() => Assert.AreEqual(8, RunGrowth([10, 20, 20, 20, 5, 5, 5, 5, 5]));

    [TestMethod]
    public void GrowthAfterAPauseRestartsTheTimer() => Assert.AreEqual(8, RunGrowth([10, 20, 20, 20, 30, 30, 30, 30, 30]));

    [TestMethod]
    public void WindowGoneFiresForEveryCondition()
    {
        foreach (var u in new[] { Until.Closed, Until.TitleChanged, Until.TitleMatches })
            Assert.IsTrue(Window(u, null, "a", "x", false));
        Assert.IsFalse(Window(Until.Closed, "a", "a", null, false));
    }

    [TestMethod]
    public void WindowTitleChangedAndMatches()
    {
        Assert.IsFalse(Window(Until.TitleChanged, "Build 12%", "Build 12%", null, false));
        Assert.IsTrue(Window(Until.TitleChanged, "Build 13%", "Build 12%", null, false));
        Assert.IsTrue(Window(Until.TitleMatches, "Render DONE", "x", "done", false));
        Assert.IsFalse(Window(Until.TitleMatches, "Render 99%", "x", "done", false));
        Assert.IsTrue(Window(Until.TitleMatches, "Epoch 20/20", "x", @"epoch (\d+)/\1", true));
        Assert.IsFalse(Window(Until.TitleMatches, "Epoch 19/20", "x", @"epoch (\d+)/\1", true));
    }

    [TestMethod]
    public void EvilRegexTimesOutAsNoMatch()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsFalse(Matches(new string('a', 40000) + "!", "^(a+)+$", true));
        Assert.IsLessThan(5000, sw.ElapsedMilliseconds);
        Assert.IsNotNull(RegexError("(unclosed"));
        Assert.IsNull(RegexError("ok.*"));
    }
}

[TestClass]
public class RouterTests
{
    static Router.Env E(bool away = false, bool busy = false, bool dnd = false, bool phone = true) => new(away, busy, dnd, phone);

    [TestMethod]
    public void AutoAtThePcIsToastOnly() => Assert.AreEqual((true, false), Router.Decide(Route.Auto, E()));

    [TestMethod]
    public void AutoAwayOrBusyAddsThePhone()
    {
        Assert.AreEqual((true, true), Router.Decide(Route.Auto, E(away: true)));
        Assert.AreEqual((true, true), Router.Decide(Route.Auto, E(busy: true)));
    }

    [TestMethod]
    public void DoNotDisturbSilencesThePhoneOnly()
    {
        Assert.AreEqual((true, false), Router.Decide(Route.Auto, E(away: true, dnd: true)));
        Assert.AreEqual((true, false), Router.Decide(Route.Both, E(dnd: true)));
    }

    [TestMethod]
    public void ExplicitRoutes()
    {
        Assert.AreEqual((true, false), Router.Decide(Route.Pc, E(away: true)));
        Assert.AreEqual((false, true), Router.Decide(Route.Phone, E()));
        Assert.AreEqual((true, true), Router.Decide(Route.Both, E()));
    }

    [TestMethod]
    public void NoBotFallsBackToTheToast()
    {
        Assert.AreEqual((true, false), Router.Decide(Route.Phone, E(phone: false)));
        Assert.AreEqual((true, false), Router.Decide(Route.Auto, E(away: true, phone: false)));
    }
}
