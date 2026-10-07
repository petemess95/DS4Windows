using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;

namespace DS4WindowsTests;

/// <summary>
/// Aim macro settings (Task 7.1): the &lt;AimMacros&gt;, &lt;Recoil&gt; and
/// &lt;Rotate&gt; XML, what each builds into, the per-slot warnings and the
/// reset on profile load. Nothing reads them at runtime yet. Uses a temp
/// appdatapath and a private live store; never the real one.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimMacroSettingsTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;

    private static readonly FieldInfo StoreField =
        typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);

    internal const string SampleMacrosXml =
        "<AimMacros>" +
        "<Toggle>FnL+FnR</Toggle>" +
        "<GameDeadZone>10</GameDeadZone>" +
        "<GameDeadZoneShape>Radial</GameDeadZoneShape>" +
        "<ArmedColor>255,0,255</ArmedColor>" +
        "</AimMacros>";

    internal const string SampleRecoilXml =
        "<Recoil>" +
        "<FireThreshold>30</FireThreshold>" +
        "<Delay>0</Delay>" +
        "<Ramp>60</Ramp>" +
        "<PullY>6</PullY>" +
        "<DriftX>0</DriftX>" +
        "<Pattern>0:8,300:5,1200:4</Pattern>" +
        "</Recoil>";

    internal const string SampleRotateXml =
        "<Rotate>" +
        "<Radius>10</Radius>" +
        "<RadiusY>10</RadiusY>" +
        "<Period>60</Period>" +
        "<Direction>CW</Direction>" +
        "<WhenFiring>Any</WhenFiring>" +
        "<FadeAbove>0</FadeAbove>" +
        "</Rotate>";

    private string folder;
    private string oldAppDataPath;
    private object oldStore;
    private BackingStore live;
    private readonly List<string> warnings = new();

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-aim-macro-{Guid.NewGuid():N}");
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

    private static ControlService UninitializedService() =>
        (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));

    private string WriteProfile(string name, string body)
    {
        string path = Path.Combine(folder, "Profiles", $"{name}.xml");
        File.WriteAllText(path, $"<DS4Windows config_version=\"5\">{body}</DS4Windows>");
        return path;
    }

    private static void LoadInto(BackingStore store, string path)
    {
        Assert.IsTrue(store.LoadProfileNew(Slot, false, UninitializedService(),
            out _, path, xinputChange: false, postLoad: false));
    }

    private string SavedXml(BackingStore store, string name)
    {
        Assert.IsTrue(store.SaveProfileNew(Slot, name));
        return File.ReadAllText(Path.Combine(folder, "Profiles", $"{name}.xml"));
    }

    // Live load through the prepare path (publishes the layer set).
    private static void Load(string name)
    {
        Assert.IsTrue(Global.LoadProfile(Slot, false, UninitializedService(),
            xinputChange: false, postLoad: false, profileName: name));
    }

    private static string Layer(string trigger, string source, string macros = "") =>
        $"<AimLayer><Enabled>True</Enabled><Trigger>{trigger}</Trigger>" +
        $"<Threshold>100</Threshold><SourceProfile>{source}</SourceProfile>" +
        $"<UseSourceLightbar>True</UseSourceLightbar>{macros}</AimLayer>";

    private void SaveSource(string name) =>
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, name));

    // Base profile with one L2 layer (borrowing "Source") and optional macros.
    private void LoadBase(string layerMacros, string profileMacros = "", string name = "Base")
    {
        if (!File.Exists(Path.Combine(folder, "Profiles", "Source.xml")))
            SaveSource("Source");
        WriteProfile(name, Layer("L2", "Source", layerMacros) + profileMacros);
        Load(name);
    }

    private static AimLayerSet Current() => AimLayerState.Current(Slot);

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static int At(string text, string part) =>
        text.IndexOf(part, StringComparison.Ordinal);

    // ---- XML round trip ----

    [TestMethod]
    public void EveryElementRoundTripsInOrder()
    {
        string path = WriteProfile("Macros",
            Layer("L2", "Edge Expo", SampleRecoilXml + SampleRotateXml) +
            Layer("R2", "Edge Hipfire", SampleRotateXml) + SampleMacrosXml);
        var store = new BackingStore();
        LoadInto(store, path);

        AimMacrosConfig macros = store.aimMacros[Slot];
        Assert.IsNotNull(macros);
        Assert.AreEqual("FnL+FnR", macros.Toggle);
        Assert.AreEqual("10", macros.GameDeadZone);
        Assert.AreEqual("Radial", macros.GameDeadZoneShape);
        Assert.AreEqual("255,0,255", macros.ArmedColor);
        AimRecoilConfig recoil = store.aimLayers[Slot][0].Recoil;
        Assert.AreEqual("30", recoil.FireThreshold);
        Assert.AreEqual("0", recoil.Delay);
        Assert.AreEqual("60", recoil.Ramp);
        Assert.AreEqual("6", recoil.PullY);
        Assert.AreEqual("0", recoil.DriftX);
        Assert.AreEqual("0:8,300:5,1200:4", recoil.Pattern);
        AimRotateConfig rotate = store.aimLayers[Slot][0].Rotate;
        Assert.AreEqual("10", rotate.Radius);
        Assert.AreEqual("10", rotate.RadiusY);
        Assert.AreEqual("60", rotate.Period);
        Assert.AreEqual("CW", rotate.Direction);
        Assert.AreEqual("Any", rotate.WhenFiring);
        Assert.AreEqual("0", rotate.FadeAbove);
        Assert.IsNull(store.aimLayers[Slot][1].Recoil);
        Assert.IsNotNull(store.aimLayers[Slot][1].Rotate);

        string xml = SavedXml(store, "Macros Saved");
        Assert.AreEqual(1, Count(xml, "<AimMacros>"), xml);
        Assert.AreEqual(1, Count(xml, "<Recoil>"), xml);
        Assert.AreEqual(2, Count(xml, "<Rotate>"), xml);
        // <AimMacros> right after the last <AimLayer>.
        int lastLayerEnd = xml.LastIndexOf("</AimLayer>", StringComparison.Ordinal);
        Assert.IsTrue(At(xml, "<AimMacros>") > lastLayerEnd, xml);
        Assert.AreEqual(string.Empty,
            xml.Substring(lastLayerEnd + "</AimLayer>".Length,
                At(xml, "<AimMacros>") - lastLayerEnd - "</AimLayer>".Length).Trim(), xml);
        // Inside a block: Recoil then Rotate, after UseSourceLightbar.
        Assert.IsTrue(At(xml, "<UseSourceLightbar>") < At(xml, "<Recoil>"), xml);
        Assert.IsTrue(At(xml, "</Recoil>") < At(xml, "<Rotate>"), xml);
        Assert.IsTrue(At(xml, "</Rotate>") < At(xml, "</AimLayer>"), xml);
        string[] order =
        {
            "<Toggle>FnL+FnR</Toggle>", "<GameDeadZone>10</GameDeadZone>",
            "<GameDeadZoneShape>Radial</GameDeadZoneShape>", "<ArmedColor>255,0,255</ArmedColor>",
        };
        for (int i = 1; i < order.Length; i++)
            Assert.IsTrue(At(xml, order[i - 1]) < At(xml, order[i]), order[i]);
        string[] recoilOrder =
        {
            "<FireThreshold>30</FireThreshold>", "<Ramp>60</Ramp>", "<PullY>6</PullY>",
            "<DriftX>0</DriftX>", "<Pattern>0:8,300:5,1200:4</Pattern>",
        };
        for (int i = 1; i < recoilOrder.Length; i++)
            Assert.IsTrue(At(xml, recoilOrder[i - 1]) < At(xml, recoilOrder[i]), recoilOrder[i]);
        string[] rotateOrder =
        {
            "<Radius>10</Radius>", "<RadiusY>10</RadiusY>", "<Period>60</Period>",
            "<Direction>CW</Direction>", "<WhenFiring>Any</WhenFiring>", "<FadeAbove>0</FadeAbove>",
        };
        for (int i = 1; i < rotateOrder.Length; i++)
            Assert.IsTrue(At(xml, rotateOrder[i - 1]) < At(xml, rotateOrder[i]), rotateOrder[i]);

        // Load-save is stable.
        var again = new BackingStore();
        LoadInto(again, Path.Combine(folder, "Profiles", "Macros Saved.xml"));
        Assert.AreEqual(xml, SavedXml(again, "Macros Saved"));
    }

    [TestMethod]
    public void AbsentChildrenAreNotWritten()
    {
        string path = WriteProfile("Partial",
            Layer("L2", "Edge Expo", "<Recoil><PullY>4</PullY></Recoil>") +
            "<AimMacros><Toggle>PS</Toggle></AimMacros>");
        var store = new BackingStore();
        LoadInto(store, path);
        string xml = SavedXml(store, "Partial Saved");
        StringAssert.Contains(xml, "<PullY>4</PullY>");
        StringAssert.Contains(xml, "<Toggle>PS</Toggle>");
        foreach (string absent in new[] { "<FireThreshold>", "<Ramp>", "<Pattern>",
            "<GameDeadZone>", "<ArmedColor>", "<Rotate>", "<DriftX>" })
            Assert.AreEqual(0, Count(xml, absent), absent);
    }

    [TestMethod]
    public void BadValuesStillSaveAsWritten()
    {
        // Checked only when prepared; the file keeps what the user typed.
        string path = WriteProfile("Bad",
            Layer("L2", "Edge Expo", "<Recoil><PullY>abc</PullY></Recoil>") +
            "<AimMacros><Toggle>Nope</Toggle></AimMacros>");
        var store = new BackingStore();
        LoadInto(store, path);
        string xml = SavedXml(store, "Bad Saved");
        StringAssert.Contains(xml, "<PullY>abc</PullY>");
        StringAssert.Contains(xml, "<Toggle>Nope</Toggle>");
    }

    [TestMethod]
    public void BlockWithOnlyMacrosIsStillSaved()
    {
        var store = new BackingStore();
        store.aimLayers[Slot] = new[]
        {
            new AimLayerConfig(false, DS4Controls.L2, 100, 0, "", false,
                new AimRecoilConfig(null, null, null, "5", null, null)),
        };
        Assert.IsFalse(store.aimLayers[Slot][0].IsDefault);
        string xml = SavedXml(store, "OnlyMacros");
        StringAssert.Contains(xml, "<PullY>5</PullY>");
    }

    [TestMethod]
    public void ProfileWithoutMacrosSavesNoMacroElements()
    {
        var store = new BackingStore();
        LoadInto(store, WriteProfile("Plain", Layer("L2", "Edge Expo")));
        string xml = SavedXml(store, "Plain Saved");
        foreach (string absent in new[] { "AimMacros", "<Recoil", "<Rotate" })
            Assert.AreEqual(0, Count(xml, absent), absent);
    }

    [TestMethod]
    public void LoadClearsPreviousProfilesMacrosInStore()
    {
        var store = new BackingStore();
        LoadInto(store, WriteProfile("With",
            Layer("L2", "Edge Expo", SampleRecoilXml) + SampleMacrosXml));
        Assert.IsNotNull(store.aimMacros[Slot]);
        LoadInto(store, WriteProfile("Without", Layer("L2", "Edge Expo")));
        Assert.IsNull(store.aimMacros[Slot]);
        Assert.IsNull(store.aimLayers[Slot][0].Recoil);
    }

    // ---- Built settings, published with the layer set ----

    [TestMethod]
    public void SampleBuildsAndIsPublishedWithTheLayers()
    {
        LoadBase(SampleRecoilXml + SampleRotateXml, SampleMacrosXml);
        AimLayerSet set = Current();
        Assert.IsNotNull(set);
        AimMacroSettings macros = set.Macros;
        Assert.IsNotNull(macros);
        Assert.AreEqual("Base", macros.ProfileName);
        Assert.IsTrue(macros.HasToggle);
        CollectionAssert.AreEqual(new[] { AimMacroButton.FnL, AimMacroButton.FnR },
            macros.Toggle.ToArray());
        Assert.AreEqual(0.10, macros.GameDeadZone, 1e-12);
        Assert.AreEqual(AimMacroDeadZoneShape.Radial, macros.GameDeadZoneShape);
        Assert.IsTrue(macros.HasArmedColor);
        Assert.AreEqual(new DS4Color(255, 0, 255), macros.ArmedColor);

        AimMacroRecoil recoil = set[0].Recoil;
        Assert.AreEqual((byte)30, recoil.FireThreshold);
        Assert.AreEqual(0, recoil.DelayMs);
        Assert.AreEqual(60, recoil.RampMs);
        Assert.AreEqual(0.06, recoil.PullY, 1e-12);
        Assert.AreEqual(0.0, recoil.DriftX);
        Assert.IsTrue(recoil.HasPattern);
        CollectionAssert.AreEqual(new[] { 0, 300, 1200 }, recoil.PatternTimesMs.ToArray());
        CollectionAssert.AreEqual(new[] { 0.08, 0.05, 0.04 }, recoil.PatternPullY.ToArray());

        AimMacroRotate rotate = set[0].Rotate;
        Assert.AreEqual(0.10, rotate.Radius, 1e-12);
        Assert.AreEqual(0.10, rotate.RadiusY, 1e-12);
        Assert.AreEqual(60, rotate.PeriodMs);
        Assert.IsTrue(rotate.Clockwise);
        Assert.AreEqual(AimMacroWhenFiring.Any, rotate.WhenFiring);
        Assert.AreEqual(0.0, rotate.FadeAbove);
        Assert.AreEqual(0, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void DefaultsWhenChildrenAreAbsent()
    {
        LoadBase("<Recoil></Recoil><Rotate><Radius>12.5</Radius><Direction>ccw</Direction></Rotate>",
            "<AimMacros><Toggle>ps</Toggle><GameDeadZoneShape>axial</GameDeadZoneShape></AimMacros>");
        AimLayerSet set = Current();
        Assert.AreEqual(0.0, set.Macros.GameDeadZone);
        Assert.AreEqual(AimMacroDeadZoneShape.Axial, set.Macros.GameDeadZoneShape);
        Assert.IsFalse(set.Macros.HasArmedColor);
        AimMacroRecoil recoil = set[0].Recoil;
        Assert.AreEqual((byte)30, recoil.FireThreshold);
        Assert.AreEqual(0, recoil.RampMs);
        Assert.AreEqual(0.0, recoil.PullY);
        Assert.IsFalse(recoil.HasPattern);
        AimMacroRotate rotate = set[0].Rotate;
        Assert.AreEqual(0.125, rotate.Radius, 1e-12);
        Assert.AreEqual(0.125, rotate.RadiusY, 1e-12);
        Assert.AreEqual(60, rotate.PeriodMs);
        Assert.IsFalse(rotate.Clockwise);
        Assert.AreEqual(0, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void MacrosBelongToTheLayerBlock()
    {
        // The source profile has its own aim layer with macros; they are not
        // borrowed.
        WriteProfile("Source", Layer("L2", "Other", SampleRecoilXml) + SampleMacrosXml);
        LoadBase("");
        AimLayerSet set = Current();
        Assert.IsNull(set.Macros);
        Assert.IsNull(set[0].Recoil);
        Assert.IsNull(set[0].Rotate);
    }

    [TestMethod]
    public void MacrosSurviveASourceRebuild()
    {
        LoadBase(SampleRecoilXml, SampleMacrosXml);
        AimLayerSet before = Current();
        SaveSource("Source");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ReferenceEquals(Current(), before) && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Assert.AreNotSame(before, Current());
        Assert.AreSame(before.Macros, Current().Macros);
        Assert.AreSame(before[0].Recoil, Current()[0].Recoil);
    }

    [TestMethod]
    public void ProfileLoadClearsPublishedMacros()
    {
        LoadBase(SampleRecoilXml + SampleRotateXml, SampleMacrosXml);
        Assert.IsNotNull(Current().Macros);

        // Another base with layers but no macros.
        WriteProfile("NoMacros", Layer("L2", "Source"));
        Load("NoMacros");
        Assert.IsNotNull(Current());
        Assert.IsNull(Current().Macros);
        Assert.IsNull(Current()[0].Recoil);
        Assert.IsNull(Current()[0].Rotate);

        LoadBase(SampleRecoilXml, SampleMacrosXml);
        Assert.IsNotNull(Current().Macros);
        // A profile without any aim layer clears everything.
        WriteProfile("Plain", "");
        Load("Plain");
        Assert.IsNull(Current());
        Assert.IsNull(live.aimMacros[Slot]);
    }

    // ---- Bad values: element ignored, one warning ----

    [DataTestMethod]
    [DataRow("<FireThreshold>256</FireThreshold>")]
    [DataRow("<FireThreshold>-1</FireThreshold>")]
    [DataRow("<Delay>2001</Delay>")]
    [DataRow("<Delay>1.5</Delay>")]
    [DataRow("<Ramp>1001</Ramp>")]
    [DataRow("<PullY>51</PullY>")]
    [DataRow("<PullY>-1</PullY>")]
    [DataRow("<PullY>abc</PullY>")]
    [DataRow("<PullY>NaN</PullY>")]
    [DataRow("<DriftX>50.5</DriftX>")]
    [DataRow("<DriftX>-51</DriftX>")]
    [DataRow("<Pattern></Pattern>")]
    [DataRow("<Pattern>0:8,0:5</Pattern>")]
    [DataRow("<Pattern>300:8,100:5</Pattern>")]
    [DataRow("<Pattern>0:51</Pattern>")]
    [DataRow("<Pattern>-5:5</Pattern>")]
    [DataRow("<Pattern>0-5</Pattern>")]
    [DataRow("<Pattern>0:1,1:1,2:1,3:1,4:1,5:1,6:1,7:1,8:1,9:1,10:1,11:1,12:1,13:1,14:1,15:1,16:1</Pattern>")]
    public void BadRecoilIsIgnoredWithOneWarning(string child)
    {
        LoadBase($"<Recoil><PullY>5</PullY>{child}</Recoil>".Replace(
            "<PullY>5</PullY><PullY>", "<PullY>"), SampleMacrosXml);
        AimLayerSet set = Current();
        Assert.IsNotNull(set);
        Assert.IsNull(set[0].Recoil);
        Assert.IsNotNull(set.Macros);
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "<Recoil> ignored");
        StringAssert.Contains(warnings[0], "\"Base\"");

        // Re-applying the same profile does not log it again.
        Load("Base");
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void SixteenPatternPointsAreAllowed()
    {
        string points = string.Join(",", Enumerable.Range(0, 16).Select(i => $"{i * 10}:{i}"));
        LoadBase($"<Recoil><Pattern>{points}</Pattern></Recoil>");
        Assert.AreEqual(16, Current()[0].Recoil.PatternTimesMs.Length);
        Assert.AreEqual(0, warnings.Count, string.Join("\n", warnings));
    }

    [DataTestMethod]
    [DataRow("<Radius>51</Radius>")]
    [DataRow("<Radius>-1</Radius>")]
    [DataRow("<Radius>x</Radius>")]
    [DataRow("<Radius>10</Radius><RadiusY>60</RadiusY>")]
    [DataRow("<Radius>10</Radius><Period>9</Period>")]
    [DataRow("<Radius>10</Radius><Period>1001</Period>")]
    [DataRow("<Radius>10</Radius><Direction>Left</Direction>")]
    [DataRow("<Radius>10</Radius><WhenFiring>Sometimes</WhenFiring>")]
    [DataRow("<Radius>10</Radius><WhenFiring>1</WhenFiring>")]
    [DataRow("<Radius>10</Radius><FadeAbove>101</FadeAbove>")]
    [DataRow("<Period>60</Period>")]
    public void BadRotateIsIgnoredWithOneWarning(string children)
    {
        LoadBase($"{SampleRecoilXml}<Rotate>{children}</Rotate>");
        AimLayerSet set = Current();
        Assert.IsNull(set[0].Rotate);
        Assert.IsNotNull(set[0].Recoil);
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "<Rotate> ignored");
        Load("Base");
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
    }

    [DataTestMethod]
    [DataRow("<GameDeadZone>91</GameDeadZone>")]
    [DataRow("<GameDeadZone>-1</GameDeadZone>")]
    [DataRow("<GameDeadZone>ten</GameDeadZone>")]
    [DataRow("<GameDeadZoneShape>Square</GameDeadZoneShape>")]
    [DataRow("<ArmedColor>255,0</ArmedColor>")]
    [DataRow("<ArmedColor>256,0,0</ArmedColor>")]
    public void BadAimMacrosIsIgnoredWithOneWarning(string child)
    {
        LoadBase(SampleRecoilXml, $"<AimMacros><Toggle>FnL+FnR</Toggle>{child}</AimMacros>");
        AimLayerSet set = Current();
        Assert.IsNull(set.Macros);
        // Layer macros are separate elements and still build.
        Assert.IsNotNull(set[0].Recoil);
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "<AimMacros> ignored");
        Load("Base");
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
    }

    [DataTestMethod]
    [DataRow("<Toggle>FnL+Banana</Toggle>")]
    [DataRow("<Toggle>Cross+Circle+Square+Triangle+L1</Toggle>")]
    [DataRow("<Toggle></Toggle>")]
    [DataRow("<Toggle>FnL+</Toggle>")]
    [DataRow("<Toggle>FnL+fnl</Toggle>")]
    [DataRow("")]
    public void BadToggleKeepsMacrosButTheyCannotArm(string toggle)
    {
        LoadBase(SampleRecoilXml, $"<AimMacros>{toggle}<GameDeadZone>12</GameDeadZone></AimMacros>");
        AimMacroSettings macros = Current().Macros;
        Assert.IsNotNull(macros);
        Assert.IsFalse(macros.HasToggle);
        Assert.AreEqual(0, macros.Toggle.Length);
        Assert.AreEqual(0.12, macros.GameDeadZone, 1e-12);
        Assert.IsFalse(macros.IsToggleDown(new DS4State { FnL = true, FnR = true }));
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "toggle");
        Load("Base");
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void FixedProfileWarnsAgainIfBrokenLater()
    {
        LoadBase("<Recoil><PullY>99</PullY></Recoil>");
        Assert.AreEqual(1, warnings.Count);
        LoadBase("<Recoil><PullY>9</PullY></Recoil>");
        Assert.AreEqual(1, warnings.Count);
        LoadBase("<Recoil><PullY>99</PullY></Recoil>");
        Assert.AreEqual(2, warnings.Count);
    }

    // ---- Toggle grammar ----

    [DataTestMethod]
    [DataRow("FnL+FnR", "FnL,FnR")]
    [DataRow(" fnl + FNR ", "FnL,FnR")]
    [DataRow("PS", "PS")]
    [DataRow("Touch+Share", "TouchButton,Share")]
    [DataRow("BLP+BRP+DpadUp+R3", "BLP,BRP,DpadUp,R3")]
    public void ValidTogglesParse(string text, string expected)
    {
        Assert.IsTrue(AimMacroParser.TryParseToggle(text, out AimMacroButton[] buttons, out string error));
        Assert.IsNull(error);
        Assert.AreEqual(expected, string.Join(",", buttons));
    }

    [DataTestMethod]
    [DataRow("FnL+Banana", "unknown")]
    [DataRow("L2", "unknown")]
    [DataRow("Cross+Circle+Square+Triangle+L1", "at most 4")]
    [DataRow("", "missing or empty")]
    [DataRow("   ", "missing or empty")]
    [DataRow(null, "missing or empty")]
    [DataRow("FnL++FnR", "unknown")]
    [DataRow("FnL+FnL", "repeated")]
    public void BadTogglesAreRejected(string text, string reason)
    {
        Assert.IsFalse(AimMacroParser.TryParseToggle(text, out AimMacroButton[] buttons, out string error));
        Assert.AreEqual(0, buttons.Length);
        StringAssert.Contains(error, reason);
    }

    [TestMethod]
    public void EveryToggleButtonReadsItsDS4StateField()
    {
        foreach (AimMacroButton button in Enum.GetValues<AimMacroButton>())
        {
            var state = new DS4State();
            Assert.IsFalse(AimMacroParser.IsDown(state, button), button.ToString());
            FieldInfo field = typeof(DS4State).GetField(button.ToString());
            Assert.IsNotNull(field, button.ToString());
            field.SetValue(state, true);
            Assert.IsTrue(AimMacroParser.IsDown(state, button), button.ToString());
        }
    }

    [TestMethod]
    public void IsToggleDownNeedsEveryButton()
    {
        Assert.IsTrue(AimMacroParser.TryParseToggle("FnL+FnR", out AimMacroButton[] toggle, out _));
        var macros = new AimMacroSettings("P", toggle, 0, AimMacroDeadZoneShape.Radial, null);
        Assert.IsFalse(macros.IsToggleDown(new DS4State { FnL = true }));
        Assert.IsTrue(macros.IsToggleDown(new DS4State { FnL = true, FnR = true }));
    }
}
