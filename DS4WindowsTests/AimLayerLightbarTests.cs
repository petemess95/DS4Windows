using System.Reflection;
using DS4Windows;
using DS4Windows.DS4Control;

namespace DS4WindowsTests;

/// <summary>
/// Aim layer lightbar cue (Tasks 3.4, 5.2): with UseSourceLightbar set, the
/// plain main colour becomes the active layer's source colour, as decided by
/// Mapping.SetCurveAndDeadzone for the same report. Custom, battery gradient
/// and the no-layer path are unchanged.
/// Uses a temp appdatapath and a private live store; never the real one.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimLayerLightbarTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;

    private static readonly FieldInfo StoreField =
        typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly DS4Color BaseColor = new(10, 20, 30);
    private static readonly DS4Color SourceColor = new(200, 0, 150);

    private string folder;
    private string oldAppDataPath;
    private object oldStore;
    private readonly object owner = new();

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-aim-light-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Profiles"));
        oldAppDataPath = Global.appdatapath;
        Global.appdatapath = folder;
        oldStore = StoreField.GetValue(null);
        StoreField.SetValue(null, new BackingStore());
        AimLayerState.Clear(Slot);
        AimLayerState.ResetSelectionForTests(Slot);
        Mapping.ResetStickFilters(Slot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        AimLayerState.Clear(Slot);
        AimLayerState.ResetSelectionForTests(Slot);
        StoreField.SetValue(null, oldStore);
        Mapping.ResetStickFilters(Slot);
        Global.appdatapath = oldAppDataPath;
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static void Publish(bool useSourceLightbar, DS4Controls trigger = DS4Controls.L2,
        byte threshold = 100)
    {
        var request = new AimLayerRequest("Base", "Source", trigger, threshold, useSourceLightbar);
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.72, 0.26, 1.00, 1.00, BezierCurve.AxisType.LSRS));
        var settings = new AimLayerStickSettings(request, new StickDeadZoneInfo(), 1.0, false,
            SquareStickInfo.DEFAULT_ROUNDNESS, 0, curve, SourceColor);
        // Same save sequence: no background rebuild (nothing to read).
        AimLayerState.Publish(Slot, new AimLayerPreparation(request, settings,
            AimLayerState.ReadSaveSequence()));
    }

    // One report through the stick path, which decides "layer on".
    private void Report(byte l2, byte r2)
    {
        var input = new DS4State { LX = 128, LY = 128, RX = 128, RY = 128, L2 = l2, R2 = r2 };
        Mapping.SetCurveAndDeadzone(Slot, input, new DS4State(), owner);
    }

    private static LightbarDS4WinInfo Plain() => new() { m_Led = BaseColor };

    private static DS4Color Color(LightbarDS4WinInfo info, int battery = 80) =>
        DS4LightBar.StaticBaseColor(info, Slot, battery);

    private static void AssertColor(DS4Color expected, DS4Color actual, string context)
    {
        Assert.AreEqual(expected.red, actual.red, $"red {context}");
        Assert.AreEqual(expected.green, actual.green, $"green {context}");
        Assert.AreEqual(expected.blue, actual.blue, $"blue {context}");
    }

    [TestMethod]
    public void MainColourFollowsTriggerOnEveryReport()
    {
        Publish(true);
        byte[] l2s = { 0, 100, 101, 255, 100, 0, 101, 99 };
        foreach (byte l2 in l2s)
        {
            Report(l2, 0);
            AssertColor(l2 > 100 ? SourceColor : BaseColor, Color(Plain()), $"L2 {l2}");
        }
    }

    [TestMethod]
    public void R2TriggerUsesR2Only()
    {
        Publish(true, DS4Controls.R2, 50);
        Report(255, 50);
        AssertColor(BaseColor, Color(Plain()), "L2 held, R2 at threshold");
        Report(0, 51);
        AssertColor(SourceColor, Color(Plain()), "R2 above threshold");
    }

    [TestMethod]
    public void UseSourceLightbarOffKeepsBaseColour()
    {
        Publish(false);
        foreach (byte l2 in new byte[] { 0, 101, 255 })
        {
            Report(l2, 0);
            AssertColor(BaseColor, Color(Plain()), $"L2 {l2}");
        }
    }

    [TestMethod]
    public void NoLayerKeepsBaseColour()
    {
        foreach (byte l2 in new byte[] { 0, 101, 255 })
        {
            Report(l2, 0);
            AssertColor(BaseColor, Color(Plain()), $"L2 {l2}");
        }

        // A layer cleared while the trigger was held stops the cue at once.
        Publish(true);
        Report(255, 0);
        AssertColor(SourceColor, Color(Plain()), "held");
        AimLayerState.Clear(Slot);
        AssertColor(BaseColor, Color(Plain()), "cleared");
    }

    [TestMethod]
    public void BatteryGradientAndCustomColourAreNotAffected()
    {
        var battery = new LightbarDS4WinInfo
        {
            ledAsBattery = true, m_Led = BaseColor, m_LowLed = new DS4Color(255, 0, 0),
        };
        var custom = new LightbarDS4WinInfo
        {
            useCustomLed = true, m_Led = BaseColor, m_CustomLed = new DS4Color(1, 2, 3),
        };
        DS4Color batteryOff = Color(battery, 40);
        DS4Color customOff = Color(custom);

        Publish(true);
        Report(255, 0);
        AssertColor(SourceColor, Color(Plain()), "plain main colour swaps");
        AssertColor(batteryOff, Color(battery, 40), "battery gradient");
        AssertColor(customOff, Color(custom), "custom colour");
    }

    private static readonly DS4Color ExpoColor = new(0, 255, 0);
    private static readonly DS4Color HipfireColor = new(255, 0, 0);

    // L2 -> Expo (no delay) first, then R2 -> Hipfire after 100 ms.
    private static void PublishTwo(bool expoLightbar, bool hipfireLightbar)
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.72, 0.26, 1.00, 1.00, BezierCurve.AxisType.LSRS));
        var requests = new[]
        {
            new AimLayerRequest("Base", "Expo", DS4Controls.L2, 100, expoLightbar, 0, 0),
            new AimLayerRequest("Base", "Hipfire", DS4Controls.R2, 100, hipfireLightbar, 100, 1),
        };
        var built = new[]
        {
            new AimLayerStickSettings(requests[0], new StickDeadZoneInfo(), 1.0, false,
                SquareStickInfo.DEFAULT_ROUNDNESS, 0, curve, ExpoColor),
            new AimLayerStickSettings(requests[1], new StickDeadZoneInfo(), 1.0, false,
                SquareStickInfo.DEFAULT_ROUNDNESS, 0, curve, HipfireColor),
        };
        AimLayerState.Publish(Slot, new AimLayerPreparation(requests, built,
            AimLayerState.ReadSaveSequence()));
    }

    private void Report(long nowMs, byte l2, byte r2)
    {
        var input = new DS4State { LX = 128, LY = 128, RX = 128, RY = 128, L2 = l2, R2 = r2 };
        Mapping.SetCurveAndDeadzone(Slot, input, new DS4State(), owner, nowMs);
    }

    // (ms, L2, R2, which colour: 0 base, 1 Expo, 2 Hipfire).
    private static readonly (long ms, byte l2, byte r2, int layer)[] TwoLayerSequence =
    {
        (0, 0, 0, 0),
        (1, 0, 255, 0),     // R2 pressed: base until 100 ms
        (100, 0, 255, 0),
        (101, 0, 255, 2),   // Hipfire
        (150, 255, 255, 1), // L2 wins
        (151, 0, 255, 2),   // L2 released, R2 past the delay: Hipfire at once
        (152, 0, 0, 0),     // R2 released: base
        (153, 255, 0, 1),
        (154, 0, 0, 0),
    };

    private void RunTwoLayerSequence(DS4Color expoShown, DS4Color hipfireShown)
    {
        foreach (var step in TwoLayerSequence)
        {
            Report(10_000 + step.ms, step.l2, step.r2);
            DS4Color expected = step.layer == 1 ? expoShown :
                step.layer == 2 ? hipfireShown : BaseColor;
            AssertColor(expected, Color(Plain()), $"+{step.ms} ms, L2 {step.l2}, R2 {step.r2}");
        }
    }

    [TestMethod]
    public void MainColourFollowsTheActiveLayer()
    {
        PublishTwo(true, true);
        RunTwoLayerSequence(ExpoColor, HipfireColor);
    }

    [TestMethod]
    public void UseSourceLightbarOffOnOneLayerOnlyAffectsThatLayer()
    {
        PublishTwo(true, false);
        RunTwoLayerSequence(ExpoColor, BaseColor);

        PublishTwo(false, true);
        RunTwoLayerSequence(BaseColor, HipfireColor);
    }

    [TestMethod]
    public void HipfireLeavesBatteryGradientAndCustomColourAlone()
    {
        var battery = new LightbarDS4WinInfo
        {
            ledAsBattery = true, m_Led = BaseColor, m_LowLed = new DS4Color(255, 0, 0),
        };
        var custom = new LightbarDS4WinInfo
        {
            useCustomLed = true, m_Led = BaseColor, m_CustomLed = new DS4Color(1, 2, 3),
        };
        DS4Color batteryOff = Color(battery, 40);
        DS4Color customOff = Color(custom);

        PublishTwo(true, true);
        Report(0, 0, 255);
        Report(100, 0, 255);
        AssertColor(HipfireColor, Color(Plain()), "plain main colour swaps");
        AssertColor(batteryOff, Color(battery, 40), "battery gradient");
        AssertColor(customOff, Color(custom), "custom colour");
        Report(101, 255, 255);
        AssertColor(ExpoColor, Color(Plain()), "plain main colour swaps");
        AssertColor(batteryOff, Color(battery, 40), "battery gradient, Expo");
        AssertColor(customOff, Color(custom), "custom colour, Expo");
    }

    [TestMethod]
    public void NewPublishOrClearDropsAStaleActiveColour()
    {
        PublishTwo(true, true);
        Report(0, 0, 255);
        Report(100, 0, 255);
        AssertColor(HipfireColor, Color(Plain()), "Hipfire");

        // Profile applied again: no report yet, so no layer is active.
        PublishTwo(true, true);
        AssertColor(BaseColor, Color(Plain()), "after publish");
        Report(101, 0, 255);
        AssertColor(BaseColor, Color(Plain()), "timer restarted");

        Report(201, 0, 255);
        AssertColor(HipfireColor, Color(Plain()), "Hipfire again");
        AimLayerState.Clear(Slot);
        AssertColor(BaseColor, Color(Plain()), "cleared");
    }
}
