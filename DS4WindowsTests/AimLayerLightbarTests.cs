using System.Reflection;
using DS4Windows;
using DS4Windows.DS4Control;

namespace DS4WindowsTests;

/// <summary>
/// Aim layer lightbar cue (Task 3.4): with UseSourceLightbar set, the plain
/// main colour becomes the source profile's colour while the trigger is held,
/// as decided by Mapping.SetCurveAndDeadzone for the same report. Custom,
/// battery gradient and the no-layer path are unchanged.
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
        AimLayerState.SetHeld(Slot, false);
        Mapping.ResetStickFilters(Slot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        AimLayerState.Clear(Slot);
        AimLayerState.SetHeld(Slot, false);
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
}
