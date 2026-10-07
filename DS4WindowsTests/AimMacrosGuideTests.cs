using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DS4Windows;

namespace DS4WindowsTests;

/// <summary>
/// Every XML sample in docs/aim-macros.md (Task 7.4) is read from the guide
/// itself, loaded as a profile and checked: no macro warnings and the
/// expected built settings. Uses a temp appdatapath and a private live store.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimMacrosGuideTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;

    private static readonly FieldInfo StoreField =
        typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);

    private string folder;
    private string oldAppDataPath;
    private object oldStore;
    private BackingStore live;
    private readonly List<string> warnings = new();

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-aim-guide-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Profiles"));
        oldAppDataPath = Global.appdatapath;
        Global.appdatapath = folder;
        oldStore = StoreField.GetValue(null);
        live = new BackingStore();
        StoreField.SetValue(null, live);
        AimLayerState.Clear(Slot);
        AppLogger.GuiLog += CaptureLog;
    }

    [TestCleanup]
    public void Cleanup()
    {
        AppLogger.GuiLog -= CaptureLog;
        AimLayerState.Clear(Slot);
        StoreField.SetValue(null, oldStore);
        Global.appdatapath = oldAppDataPath;
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private void CaptureLog(object sender, DebugEventArgs e)
    {
        if (e.Data.StartsWith("Aim ", StringComparison.Ordinal))
            lock (warnings) warnings.Add(e.Data);
    }

    private static string GuidePath([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "..", "docs", "aim-macros.md"));

    private static string Guide() => File.ReadAllText(GuidePath()).Replace("\r\n", "\n");

    // Fenced blocks of the given info string ("xml" or "" for plain).
    // Fences are paired in order, so a closing fence is never taken as an opening one.
    private static List<string> Blocks(string info) =>
        Regex.Matches(Guide(), "^```(\\w*)\n(.*?)^```$", RegexOptions.Multiline | RegexOptions.Singleline)
            .Where(m => m.Groups[1].Value == info)
            .Select(m => m.Groups[2].Value.Trim()).ToList();

    private static List<string> XmlSamples() => Blocks("xml");

    private static string EdgeLinearSample() =>
        XmlSamples().Single(s => s.StartsWith("<AimLayer>", StringComparison.Ordinal));

    private static string ReferenceSample(string root) =>
        XmlSamples().Single(s => s.StartsWith($"<{root}>", StringComparison.Ordinal));

    private void WriteProfile(string name, string body) =>
        File.WriteAllText(Path.Combine(folder, "Profiles", $"{name}.xml"),
            $"<DS4Windows config_version=\"5\">{body}</DS4Windows>");

    private void LoadWithSources(string name, string body)
    {
        foreach (string source in new[] { "Edge Expo", "Edge Hipfire" })
            Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, source));
        WriteProfile(name, body);
        Assert.IsTrue(Global.LoadProfile(Slot, false,
            (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService)),
            xinputChange: false, postLoad: false, profileName: name));
    }

    private static string L2Layer(string inner) =>
        "<AimLayer><Enabled>True</Enabled><Trigger>L2</Trigger><Threshold>100</Threshold>" +
        $"<SourceProfile>Edge Expo</SourceProfile>{inner}</AimLayer>";

    private void AssertNoWarnings() =>
        Assert.AreEqual(0, warnings.Count, string.Join("\n", warnings));

    [TestMethod]
    public void GuideHasExactlyTheExpectedXmlSamples()
    {
        List<string> samples = XmlSamples();
        Assert.AreEqual(4, samples.Count, string.Join("\n----\n", samples));
        Assert.AreEqual(1, samples.Count(s => s.StartsWith("<AimMacros>", StringComparison.Ordinal)));
        Assert.AreEqual(1, samples.Count(s => s.StartsWith("<Recoil>", StringComparison.Ordinal)));
        Assert.AreEqual(1, samples.Count(s => s.StartsWith("<Rotate>", StringComparison.Ordinal)));
        Assert.AreEqual(1, samples.Count(s => s.StartsWith("<AimLayer>", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AimMacrosReferenceSampleBuilds()
    {
        LoadWithSources("Ref", L2Layer("") + ReferenceSample("AimMacros"));
        AimMacroSettings macros = AimLayerState.Current(Slot).Macros;
        Assert.IsNotNull(macros);
        CollectionAssert.AreEqual(new[] { AimMacroButton.FnL, AimMacroButton.FnR }, macros.Toggle.ToArray());
        Assert.AreEqual(0.10, macros.GameDeadZone, 1e-12);
        Assert.AreEqual(AimMacroDeadZoneShape.Radial, macros.GameDeadZoneShape);
        Assert.IsTrue(macros.HasArmedColor);
        Assert.AreEqual(new DS4Color(255, 0, 255), macros.ArmedColor);
        AssertNoWarnings();
    }

    [TestMethod]
    public void RecoilReferenceSampleBuilds()
    {
        LoadWithSources("Ref", L2Layer(ReferenceSample("Recoil")));
        AimMacroRecoil recoil = AimLayerState.Current(Slot)[0].Recoil;
        Assert.IsNotNull(recoil);
        Assert.AreEqual((byte)30, recoil.FireThreshold);
        Assert.AreEqual(0, recoil.DelayMs);
        Assert.AreEqual(60, recoil.RampMs);
        Assert.AreEqual(0.06, recoil.PullY, 1e-12);
        Assert.AreEqual(0.0, recoil.DriftX);
        CollectionAssert.AreEqual(new[] { 0, 300, 1200 }, recoil.PatternTimesMs.ToArray());
        CollectionAssert.AreEqual(new[] { 0.08, 0.05, 0.04 }, recoil.PatternPullY.ToArray());
        AssertNoWarnings();
    }

    [TestMethod]
    public void RotateReferenceSampleBuilds()
    {
        LoadWithSources("Ref", L2Layer(ReferenceSample("Rotate")));
        AimMacroRotate rotate = AimLayerState.Current(Slot)[0].Rotate;
        Assert.IsNotNull(rotate);
        Assert.AreEqual(0.10, rotate.Radius, 1e-12);
        Assert.AreEqual(0.10, rotate.RadiusY, 1e-12);
        Assert.AreEqual(60, rotate.PeriodMs);
        Assert.IsTrue(rotate.Clockwise);
        Assert.AreEqual(AimMacroWhenFiring.Any, rotate.WhenFiring);
        Assert.AreEqual(0.0, rotate.FadeAbove);
        AssertNoWarnings();
    }

    [TestMethod]
    public void EdgeLinearSampleBuilds()
    {
        LoadWithSources("Edge Linear", EdgeLinearSample());
        AimLayerSet set = AimLayerState.Current(Slot);
        Assert.IsNotNull(set);
        Assert.AreEqual(2, live.aimLayers[Slot].Count);
        AimLayerConfig hip = live.aimLayers[Slot][1];
        Assert.AreEqual(DS4Controls.R2, hip.Trigger);
        Assert.AreEqual((byte)250, hip.Threshold);
        Assert.AreEqual(180, hip.Delay);

        AimMacroSettings macros = set.Macros;
        Assert.IsNotNull(macros);
        Assert.AreEqual("Edge Linear", macros.ProfileName);
        CollectionAssert.AreEqual(new[] { AimMacroButton.FnL, AimMacroButton.FnR }, macros.Toggle.ToArray());
        Assert.AreEqual(0.10, macros.GameDeadZone, 1e-12);
        Assert.AreEqual(new DS4Color(255, 0, 255), macros.ArmedColor);

        AimMacroRecoil recoil = set[0].Recoil;
        Assert.IsNotNull(recoil);
        Assert.AreEqual((byte)30, recoil.FireThreshold);
        Assert.AreEqual(0, recoil.DelayMs);
        Assert.AreEqual(60, recoil.RampMs);
        Assert.AreEqual(0.06, recoil.PullY, 1e-12);
        Assert.AreEqual(0.0, recoil.DriftX);
        Assert.IsFalse(recoil.HasPattern);

        AimMacroRotate rotate = set[0].Rotate;
        Assert.IsNotNull(rotate);
        Assert.AreEqual(0.10, rotate.Radius, 1e-12);
        Assert.AreEqual(0.10, rotate.RadiusY, 1e-12);
        Assert.AreEqual(60, rotate.PeriodMs);
        Assert.IsTrue(rotate.Clockwise);
        Assert.AreEqual(AimMacroWhenFiring.Any, rotate.WhenFiring);
        Assert.AreEqual(0.0, rotate.FadeAbove);

        // Hip-fire has no macros of its own.
        Assert.IsNull(set[1].Recoil);
        Assert.IsNull(set[1].Rotate);
        AssertNoWarnings();
    }

    [TestMethod]
    public void WarningExampleMatchesTheRealWarning()
    {
        string example = Blocks("").Single(b => b.StartsWith("Aim macros of profile", StringComparison.Ordinal));
        LoadWithSources("Edge Linear", L2Layer("<Recoil><PullY>60</PullY></Recoil>"));
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        Assert.AreEqual(example, warnings[0]);
    }
}
