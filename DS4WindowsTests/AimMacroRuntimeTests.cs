using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

/// <summary>
/// Task 7.3: aim macros at run time. Arming on the toggle's rising edge, every
/// reset case (startup, load, reload, temp profile, disconnect), the report
/// hook (ControlService.ApplyAimMacros) on the plain and MapCustom paths, the
/// armed lightbar colour, the arm/disarm log lines and zero allocation.
/// Reports are modelled stage by stage like OnReportCore (SetCurveAndDeadzone,
/// then MapCustom or the plain path, then the hook), without HID or output IO.
/// Uses a temp appdatapath and private stores; never the real ones.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimMacroRuntimeTests
{
    // A live slot (MapCustom needs a controller entry) and the editor slot
    // for real profile loads (as AimMacroSettingsTests does).
    private const int Slot = 6;
    private const int LoadSlot = Global.TEST_PROFILE_INDEX;

    private static readonly FieldInfo StoreField =
        typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly DS4Color BaseColor = new(10, 20, 30);
    private static readonly DS4Color SourceColor = new(200, 0, 150);
    private static readonly DS4Color ArmedColor = new(255, 0, 255);

    private const string MacrosXml =
        "<AimMacros><Toggle>FnL+FnR</Toggle><GameDeadZone>10</GameDeadZone>" +
        "<GameDeadZoneShape>Radial</GameDeadZoneShape><ArmedColor>255,0,255</ArmedColor></AimMacros>";
    private const string LayerMacrosXml =
        "<Recoil><FireThreshold>30</FireThreshold><Ramp>60</Ramp><PullY>6</PullY></Recoil>" +
        "<Rotate><Radius>10</Radius><Period>60</Period></Rotate>";

    private string folder;
    private string oldAppDataPath;
    private object oldStore;
    private VirtualKBMBase previousHandler;
    private VirtualKBMMapping previousKbmMapping;
    private DS4StateFieldMapping previousFields, previousOutputFields;
    private Mapping.SyntheticState previousDeviceState;
    private ControlService service;
    private Mouse mouse;
    private readonly object owner = new();
    private readonly List<string> logs = new();

    // The report loop's per-slot buffers: TempState, MappedState and the
    // macro output copy.
    private readonly DS4State temp = new();
    private readonly DS4State mapped = new();
    private readonly DS4State scratch = new();

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-aim-macro-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Profiles"));
        oldAppDataPath = Global.appdatapath;
        Global.appdatapath = folder;
        oldStore = StoreField.GetValue(null);
        StoreField.SetValue(null, new BackingStore());

        previousHandler = Global.outputKBMHandler;
        previousKbmMapping = Global.outputKBMMapping;
        previousFields = Mapping.fieldMappings[Slot];
        previousOutputFields = Mapping.outputFieldMappings[Slot];
        previousDeviceState = Mapping.deviceState[Slot];
        var kbm = new SendInputMapping();
        kbm.PopulateConstants();
        kbm.PopulateMappings();
        Global.outputKBMMapping = kbm;
        Mapping.fieldMappings[Slot] = new();
        Mapping.outputFieldMappings[Slot] = new();
        Mapping.deviceState[Slot] = new();
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1,
            Switch2Transport.Usb, out var runtime, out _));
        runtime.Synced = true;
        mouse = new Mouse(Slot, runtime);
        service = UninitializedService();
        service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        service.DS4Controllers[Slot] = runtime;

        ResetSlots();
        AppLogger.GuiLog += CaptureLog;
    }

    [TestCleanup]
    public void Cleanup()
    {
        AppLogger.GuiLog -= CaptureLog;
        ResetSlots();
        Global.useTempProfile[LoadSlot] = false;
        Global.tempprofilename[LoadSlot] = string.Empty;
        StoreField.SetValue(null, oldStore);
        Global.outputKBMHandler = previousHandler;
        Global.outputKBMMapping = previousKbmMapping;
        Mapping.fieldMappings[Slot] = previousFields;
        Mapping.outputFieldMappings[Slot] = previousOutputFields;
        Mapping.deviceState[Slot] = previousDeviceState;
        Global.appdatapath = oldAppDataPath;
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static void ResetSlots()
    {
        foreach (int slot in new[] { Slot, LoadSlot })
        {
            AimLayerState.Clear(slot);
            AimLayerState.ResetSelectionForTests(slot);
            AimMacroRuntime.ResetForTests(slot);
            Mapping.ResetStickFilters(slot);
        }
    }

    private void CaptureLog(object sender, DebugEventArgs e)
    {
        if (e.Data.StartsWith(AimMacroRuntime.ArmedMessage, StringComparison.Ordinal) ||
            e.Data.StartsWith(AimMacroRuntime.DisarmedMessage, StringComparison.Ordinal))
            lock (logs) logs.Add(e.Data);
    }

    private static ControlService UninitializedService() =>
        (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));

    // ---- Settings and publishing ------------------------------------------

    private static AimMacroSettings Macros(string profile = "Base", bool toggle = true,
        DS4Color? armedColor = null) =>
        new(profile, toggle ? new[] { AimMacroButton.FnL, AimMacroButton.FnR } : null,
            0.1, AimMacroDeadZoneShape.Radial, armedColor);

    private static AimMacroRecoil Recoil(int ramp = 0) => new(30, 0, ramp, 0.06, 0.0, null, null);

    private static AimMacroRotate Rotate() => new(0.1, 0.1, 60, true, AimMacroWhenFiring.Any, 0.0);

    private sealed record LayerSpec(DS4Controls Trigger, bool Lightbar = true,
        AimMacroRecoil Recoil = null, AimMacroRotate Rotate = null);

    private static AimLayerStickSettings[] Publish(int slot, AimMacroSettings macros,
        params LayerSpec[] specs)
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.72, 0.26, 1.00, 1.00, BezierCurve.AxisType.LSRS));
        var requests = new AimLayerRequest[specs.Length];
        var built = new AimLayerStickSettings[specs.Length];
        for (int index = 0; index < specs.Length; index++)
        {
            LayerSpec spec = specs[index];
            requests[index] = new AimLayerRequest("Base", "Source", spec.Trigger, 100,
                spec.Lightbar, 0, index, spec.Recoil, spec.Rotate);
            // Mode 6 (the Expo curve), so the stick is not a straight copy.
            built[index] = new AimLayerStickSettings(requests[index], new StickDeadZoneInfo(),
                1.0, false, SquareStickInfo.DEFAULT_ROUNDNESS, 6, curve, SourceColor);
        }
        // Same save sequence: no background rebuild (nothing to read).
        AimLayerState.Publish(slot, new AimLayerPreparation(requests, built,
            AimLayerState.ReadSaveSequence(), macros));
        return built;
    }

    private static LayerSpec L2Macros(bool lightbar = true) =>
        new(DS4Controls.L2, lightbar, Recoil(), Rotate());

    // ---- Reports ----------------------------------------------------------

    private static DS4State Input(byte rx = 128, byte ry = 128, byte l2 = 0, byte r2 = 0,
        bool fnL = false, bool fnR = false, bool cross = false) =>
        new()
        {
            LX = 140, LY = 100, RX = rx, RY = ry, L2 = l2, R2 = r2,
            FnL = fnL, FnR = fnR, Cross = cross, elapsedTime = .001,
        };

    private static DS4State Toggle(byte l2 = 0, byte r2 = 0) => Input(l2: l2, r2: r2, fnL: true, fnR: true);

    /// <summary>
    /// One report, stage by stage as OnReportCore runs it. Checks on every
    /// report that the hook leaves the input, TempState and MappedState
    /// exactly as they were, and that it returns the state it was given
    /// unless it changed the stick (then the scratch copy, which differs only
    /// in the right stick).
    /// </summary>
    private DS4State Frame(DS4State input, long nowMs, bool custom,
        OutContType output = OutContType.ViiperDS4)
    {
        DS4State cState = Mapping.SetCurveAndDeadzone(Slot, input, temp, owner, nowMs);
        if (custom)
        {
            Mapping.MapCustom(Slot, cState, mapped, new DS4StateExposed(cState), mouse, service,
                nowMs * (Stopwatch.Frequency / 1000));
            cState.CopyExtrasTo(mapped);
            cState = mapped;
        }
        Assert.AreSame(custom ? mapped : temp, cState);

        var inputBefore = new DS4State(input);
        var tempBefore = new DS4State(temp);
        var mappedBefore = new DS4State(mapped);
        var stateBefore = new DS4State(cState);
        DS4State result = ControlService.ApplyAimMacros(Slot, input, cState, scratch, nowMs, output);
        AssertSameFields(inputBefore, input, "input (device state)");
        AssertSameFields(tempBefore, temp, "TempState");
        AssertSameFields(mappedBefore, mapped, "MappedState");
        if (!ReferenceEquals(result, cState))
        {
            Assert.AreSame(scratch, result, "Macros write only into the scratch copy.");
            AssertSameFields(stateBefore, result, "scratch apart from RX/RY",
                nameof(DS4State.RXAxis), nameof(DS4State.RYAxis));
        }
        return result;
    }

    // The hook alone (arming only needs the input).
    private DS4State Step(int slot, DS4State input, long nowMs = 1000,
        OutContType output = OutContType.ViiperDS4)
    {
        var state = new DS4State();
        return ControlService.ApplyAimMacros(slot, input, state, scratch, nowMs, output);
    }

    private void Arm(int slot)
    {
        Assert.IsFalse(AimMacroRuntime.IsArmed(slot), "not armed before");
        Step(slot, Input());
        Step(slot, Toggle());
        Step(slot, Input());
        Assert.IsTrue(AimMacroRuntime.IsArmed(slot), "armed");
    }

    private static readonly FieldInfo[] StateFields = typeof(DS4State).GetFields(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static void AssertSameFields(DS4State expected, DS4State actual, string context,
        params string[] except)
    {
        foreach (FieldInfo field in StateFields)
        {
            if (Array.IndexOf(except, field.Name) >= 0)
                continue;
            object a = field.GetValue(expected), b = field.GetValue(actual);
            Assert.IsTrue(Equals(a, b), $"{context}: {field.Name} {a} != {b}");
        }
    }

    // What each output type would put on the wire.
    private static void AssertSameOutput(DS4State expected, DS4State actual, string context)
    {
        AssertSameFields(expected, actual, context);
        Assert.AreEqual(ViiperStatePacketBuilder.BuildMappedState(expected, Slot),
            ViiperStatePacketBuilder.BuildMappedState(actual, Slot), $"{context}: DS4 bytes");
        Assert.AreEqual(ViiperStatePacketBuilder.BuildXbox360State(expected, Slot),
            ViiperStatePacketBuilder.BuildXbox360State(actual, Slot), $"{context}: Xbox 360");
        Assert.AreEqual(ViiperStatePacketBuilder.BuildSwitch2State(expected, Slot),
            ViiperStatePacketBuilder.BuildSwitch2State(actual, Slot), $"{context}: Switch 2");
    }

    // ---- Byte-identical when off -------------------------------------------

    // Aiming, firing, sweeping the stick and half-pressing the toggle.
    private static IEnumerable<(DS4State input, long ms)> Sequence(bool pressToggle)
    {
        for (int index = 0; index < 240; index++)
        {
            byte l2 = (byte)(index is >= 20 and < 200 ? 255 : 0);
            byte r2 = (byte)(index is >= 60 and < 220 ? 200 : 0);
            byte rx = (byte)(64 + (index * 7) % 128);
            byte ry = (byte)(200 - (index * 3) % 90);
            bool fnL = index % 30 < 10;
            // The full combo only when the profile cannot arm with it.
            bool fnR = pressToggle && index % 30 is >= 5 and < 8;
            yield return (Input(rx, ry, l2, r2, fnL, fnR, cross: index % 9 == 0), 5_000 + index);
        }
    }

    private void AssertUntouchedRun(string context, bool pressToggle)
    {
        foreach (bool custom in new[] { false, true })
        {
            foreach (OutContType output in new[] { OutContType.ViiperDS4, OutContType.ViiperX360 })
            {
                Mapping.ResetStickFilters(Slot);
                AimLayerState.ResetSelectionForTests(Slot);
                foreach (var (input, ms) in Sequence(pressToggle))
                {
                    // Frame checks the hook returned its input object unchanged
                    // when it does not contribute; assert it never does here.
                    DS4State cState = Mapping.SetCurveAndDeadzone(Slot, input, temp, owner, ms);
                    if (custom)
                    {
                        Mapping.MapCustom(Slot, cState, mapped, new DS4StateExposed(cState), mouse,
                            service, ms * (Stopwatch.Frequency / 1000));
                        cState.CopyExtrasTo(mapped);
                        cState = mapped;
                    }
                    var expected = new DS4State(cState);
                    var inputBefore = new DS4State(input);
                    DS4State result = ControlService.ApplyAimMacros(Slot, input, cState, scratch,
                        ms, output);
                    string where = $"{context}, custom {custom}, {output}, +{ms} ms";
                    Assert.AreSame(cState, result, where);
                    AssertSameOutput(expected, result, where);
                    AssertSameFields(inputBefore, input, where + " input");
                    Assert.IsFalse(AimMacroRuntime.IsArmed(Slot), where);
                }
            }
        }
    }

    [TestMethod]
    public void NoAimLayerOrNoMacrosLeavesOutputByteIdentical()
    {
        AssertUntouchedRun("nothing published", pressToggle: true);

        // Layers with <Recoil>/<Rotate> but no <AimMacros>: never runs.
        Publish(Slot, null, L2Macros(), new LayerSpec(DS4Controls.R2, true, Recoil(), Rotate()));
        AssertUntouchedRun("layers without <AimMacros>", pressToggle: true);
    }

    [TestMethod]
    public void ConfiguredButDisarmedLeavesOutputByteIdentical()
    {
        Publish(Slot, Macros(armedColor: ArmedColor), L2Macros(),
            new LayerSpec(DS4Controls.R2, true, Recoil(), Rotate()));
        AssertUntouchedRun("configured, disarmed", pressToggle: false);

        // No valid toggle: pressing FnL+FnR never arms.
        Publish(Slot, Macros(toggle: false), L2Macros());
        AssertUntouchedRun("no toggle", pressToggle: true);
    }

    // ---- Arming -----------------------------------------------------------

    [TestMethod]
    public void HoldingTheComboFlipsOnce()
    {
        Publish(Slot, Macros("Hold"), L2Macros());
        Step(Slot, Input());
        for (int report = 0; report < 50; report++)
        {
            Step(Slot, Toggle(l2: (byte)(report * 5)), 1000 + report);
            Assert.IsTrue(AimMacroRuntime.IsArmed(Slot), $"held, report {report}");
        }

        // One button of the combo is not the combo.
        Step(Slot, Input(fnL: true));
        Step(Slot, Input(fnR: true));
        Step(Slot, Input());
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot), "partial combo");

        // FnR first, then FnL: flips when the last button goes down.
        Step(Slot, Input(fnR: true));
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot));
        Step(Slot, Toggle());
        Assert.IsFalse(AimMacroRuntime.IsArmed(Slot), "second press disarms");
        Step(Slot, Toggle());
        Step(Slot, Input(fnL: true));
        Step(Slot, Toggle());
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot), "release FnR, press again");

        WaitForLogs(3);
        CollectionAssert.AreEqual(new[]
        {
            "Aim macros armed (profile \"Hold\", controller 7)",
            "Aim macros disarmed (profile \"Hold\", controller 7)",
            "Aim macros armed (profile \"Hold\", controller 7)",
        }, Logs());
    }

    [TestMethod]
    public void StartupIsDisarmedAndAComboHeldAtLoadDoesNotArm()
    {
        // Startup state (static zero).
        Assert.IsFalse(AimMacroRuntime.IsArmed(Slot));
        Publish(Slot, Macros(), L2Macros());
        // Already held on the first report after the load: not a press.
        for (int report = 0; report < 5; report++)
            Step(Slot, Toggle());
        Assert.IsFalse(AimMacroRuntime.IsArmed(Slot));
        Step(Slot, Input());
        Step(Slot, Toggle());
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot));

        // Held through a reload of the same set: disarmed, and stays so.
        Publish(Slot, Macros(), L2Macros());
        Assert.IsFalse(AimMacroRuntime.IsArmed(Slot));
        Step(Slot, Toggle());
        Assert.IsFalse(AimMacroRuntime.IsArmed(Slot));
    }

    [TestMethod]
    public void NoTogglePublishedNeverArms()
    {
        Publish(Slot, Macros(toggle: false, armedColor: ArmedColor), L2Macros());
        Step(Slot, Input());
        Step(Slot, Toggle());
        Assert.IsFalse(AimMacroRuntime.IsArmed(Slot));
        Assert.AreEqual(0, Logs().Length);
    }

    // ---- Every reset case through real profile loads -------------------------

    private string WriteProfile(string name, string body)
    {
        string path = Path.Combine(folder, "Profiles", $"{name}.xml");
        File.WriteAllText(path, $"<DS4Windows config_version=\"5\">{body}</DS4Windows>");
        return path;
    }

    private static string Layer(string trigger, string macros) =>
        $"<AimLayer><Enabled>True</Enabled><Trigger>{trigger}</Trigger>" +
        "<Threshold>100</Threshold><SourceProfile>Source</SourceProfile>" +
        $"<UseSourceLightbar>True</UseSourceLightbar>{macros}</AimLayer>";

    private static void Load(string name) =>
        Assert.IsTrue(Global.LoadProfile(LoadSlot, false, UninitializedService(),
            xinputChange: false, postLoad: false, profileName: name), name);

    private void WriteProfiles()
    {
        Assert.IsTrue(new BackingStore().SaveProfileNew(LoadSlot, "Source"));
        WriteProfile("Base", Layer("L2", LayerMacrosXml) + MacrosXml);
        WriteProfile("Plain", Layer("L2", string.Empty));
    }

    [TestMethod]
    public void EveryProfileLoadAndDisconnectDisarms()
    {
        WriteProfiles();
        const string Disarmed = "Aim macros disarmed (profile \"Base\", controller 9)";

        Load("Base");
        Assert.IsNotNull(AimLayerState.Current(LoadSlot)?.Macros, "Base has macros");
        Assert.IsFalse(AimMacroRuntime.IsArmed(LoadSlot), "after first load");
        Arm(LoadSlot);

        Load("Base");
        Assert.IsFalse(AimMacroRuntime.IsArmed(LoadSlot), "reload of the same profile");
        Arm(LoadSlot);

        Assert.IsTrue(Global.LoadTempProfile(LoadSlot, "Base", false, UninitializedService(),
            xinputChange: false));
        Assert.IsTrue(Global.useTempProfile[LoadSlot]);
        Assert.IsFalse(AimMacroRuntime.IsArmed(LoadSlot), "temp profile");
        Arm(LoadSlot);

        Load("Plain");
        Assert.IsNull(AimLayerState.Current(LoadSlot)?.Macros, "Plain has no macros");
        Assert.IsFalse(AimMacroRuntime.IsArmed(LoadSlot), "profile without macros");
        Step(LoadSlot, Input());
        Step(LoadSlot, Toggle());
        Assert.IsFalse(AimMacroRuntime.IsArmed(LoadSlot), "cannot arm without macros");

        Load("Base");
        Arm(LoadSlot);
        // Controller removed (ClearExactControllerSlot -> Suspend), then a
        // reconnect that keeps the profile (Resume).
        AimLayerState.Suspend(LoadSlot);
        Assert.IsFalse(AimMacroRuntime.IsArmed(LoadSlot), "disconnect");
        AimLayerState.Resume(LoadSlot);
        Assert.IsFalse(AimMacroRuntime.IsArmed(LoadSlot), "reconnect");
        Step(LoadSlot, Input());
        Step(LoadSlot, Toggle());
        Assert.IsTrue(AimMacroRuntime.IsArmed(LoadSlot), "a fresh press arms again");

        WaitForLogs(9);
        string armed = "Aim macros armed (profile \"Base\", controller 9)";
        CollectionAssert.AreEqual(new[]
        {
            armed, Disarmed + ": profile loaded",
            armed, Disarmed + ": profile loaded",
            armed, Disarmed + ": profile loaded",
            armed, Disarmed + ": controller removed",
            armed,
        }, Logs());
    }

    [TestMethod]
    public void DisconnectPathSuspendsTheAimLayer()
    {
        string source = ControlServiceSource();
        string clear = Between(source, "private bool ClearExactControllerSlot(",
            "slotManager.RemoveController(device, index);");
        StringAssert.Contains(clear, "AimLayerState.Suspend(index);");
    }

    // ---- Layer change mid-fire ---------------------------------------------

    [TestMethod]
    public void LayerChangeMidFireRestartsRecoilAndPhase()
    {
        AimLayerStickSettings[] layers = Publish(Slot, Macros(),
            new LayerSpec(DS4Controls.L2, true, Recoil(ramp: 100), Rotate()),
            new LayerSpec(DS4Controls.R2, true, Recoil(ramp: 100), Rotate()));
        Frame(Input(), 900, custom: false);
        Frame(Toggle(), 901, custom: false);
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot));

        Frame(Input(l2: 255, r2: 255), 1000, custom: false);
        AimMacroState state = AimMacroRuntime.StateForTests(Slot);
        Assert.AreSame(layers[0], state.Layer);
        Assert.IsTrue(state.Firing && state.Rotating);
        Assert.AreEqual(1000, state.FireStartMs);
        Assert.AreEqual(1000, state.RotateStartMs);

        Frame(Input(l2: 255, r2: 255), 1050, custom: false);
        state = AimMacroRuntime.StateForTests(Slot);
        Assert.AreEqual(1000, state.FireStartMs, "same layer keeps its timer");

        // L2 released, R2 still firing: the R2 layer takes over this report.
        Frame(Input(l2: 0, r2: 255), 1060, custom: false);
        state = AimMacroRuntime.StateForTests(Slot);
        Assert.AreSame(layers[1], state.Layer);
        Assert.AreEqual(1060, state.FireStartMs, "recoil restarts");
        Assert.AreEqual(1060, state.RotateStartMs, "phase restarts");

        Frame(Input(l2: 255, r2: 255), 1070, custom: true);
        state = AimMacroRuntime.StateForTests(Slot);
        Assert.AreSame(layers[0], state.Layer);
        Assert.AreEqual(1070, state.FireStartMs);
        Assert.AreEqual(1070, state.RotateStartMs);

        // No layer: idle state.
        Frame(Input(), 1080, custom: true);
        Assert.IsNull(AimMacroRuntime.StateForTests(Slot).Layer);
    }

    // ---- Device state is never written -------------------------------------

    [DataTestMethod]
    [DataRow(false, OutContType.ViiperDS4)]
    [DataRow(true, OutContType.ViiperDS4)]
    [DataRow(false, OutContType.ViiperX360)]
    [DataRow(true, OutContType.ViiperX360)]
    public void ArmedMacrosWriteOnlyIntoTheScratchCopy(bool custom, OutContType output)
    {
        Publish(Slot, Macros(), L2Macros());
        Frame(Input(), 900, custom, output);
        Frame(Toggle(), 901, custom, output);
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot));

        int changed = 0;
        for (int report = 0; report < 120; report++)
        {
            byte r2 = (byte)(report >= 40 ? 255 : 0);
            DS4State input = Input(rx: 150, ry: 128, l2: 255, r2: r2);
            DS4State result = Frame(input, 1000 + report, custom, output);
            if (ReferenceEquals(result, scratch))
            {
                changed++;
                if (output == OutContType.ViiperDS4)
                    Assert.IsFalse(result.RXAxis.IsHighResolution, "byte output dithers to bytes");
            }
        }
        Assert.IsTrue(changed > 100, $"the circle and pull should move the stick ({changed})");

        // Disarm: the very next report is untouched again.
        Frame(Toggle(l2: 255), 2000, custom, output);
        Assert.IsFalse(AimMacroRuntime.IsArmed(Slot));
        DS4State after = Frame(Input(rx: 150, l2: 255, r2: 255), 2001, custom, output);
        Assert.AreNotSame(scratch, after);
    }

    [TestMethod]
    public void PrecisionFollowsTheOutputType()
    {
        Assert.AreEqual(AimMacroOutputPrecision.Byte, AimMacroRuntime.PrecisionFor(OutContType.ViiperDS4));
        Assert.AreEqual(AimMacroOutputPrecision.Byte, AimMacroRuntime.PrecisionFor(OutContType.DS4));
        Assert.AreEqual(AimMacroOutputPrecision.Byte, AimMacroRuntime.PrecisionFor(OutContType.ViiperDualSense));
        Assert.AreEqual(AimMacroOutputPrecision.HighRes, AimMacroRuntime.PrecisionFor(OutContType.ViiperX360));
        Assert.AreEqual(AimMacroOutputPrecision.HighRes, AimMacroRuntime.PrecisionFor(OutContType.X360));
        Assert.AreEqual(AimMacroOutputPrecision.HighRes, AimMacroRuntime.PrecisionFor(OutContType.ViiperXboxOne));
        Assert.AreEqual(AimMacroOutputPrecision.HighRes, AimMacroRuntime.PrecisionFor(OutContType.ViiperSwitch2Pro));
    }

    // ---- Lightbar -----------------------------------------------------------

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
    public void ArmedColourOnlyWhileArmedAndNoLayerIsActive()
    {
        var custom = new LightbarDS4WinInfo
        {
            useCustomLed = true, m_Led = BaseColor, m_CustomLed = new DS4Color(1, 2, 3),
        };
        var battery = new LightbarDS4WinInfo
        {
            ledAsBattery = true, m_Led = BaseColor, m_LowLed = new DS4Color(255, 0, 0),
        };
        DS4Color batteryOff = Color(battery, 40);

        Publish(Slot, Macros(armedColor: ArmedColor), L2Macros(lightbar: true));
        Frame(Input(), 1000, custom: false);
        AssertColor(BaseColor, Color(Plain()), "disarmed");

        Frame(Toggle(), 1001, custom: false);
        AssertColor(ArmedColor, Color(Plain()), "armed, no layer");
        AssertColor(new DS4Color(1, 2, 3), Color(custom), "Use Custom Color hides it");
        AssertColor(batteryOff, Color(battery, 40), "battery gradient unchanged");

        Frame(Input(l2: 255), 1002, custom: false);
        AssertColor(SourceColor, Color(Plain()), "layer active: its own colour");
        Frame(Input(), 1003, custom: false);
        AssertColor(ArmedColor, Color(Plain()), "layer released");

        Frame(Toggle(), 1004, custom: false);
        AssertColor(BaseColor, Color(Plain()), "disarmed again");

        // A layer without UseSourceLightbar shows the base colour, not armed.
        Publish(Slot, Macros(armedColor: ArmedColor), L2Macros(lightbar: false));
        Frame(Input(), 1100, custom: false);
        Frame(Toggle(), 1101, custom: false);
        AssertColor(ArmedColor, Color(Plain()), "armed");
        Frame(Input(l2: 255), 1102, custom: false);
        AssertColor(BaseColor, Color(Plain()), "layer without its own colour");

        // No ArmedColor: base colour while armed.
        Publish(Slot, Macros(), L2Macros());
        Frame(Input(), 1200, custom: false);
        Frame(Toggle(), 1201, custom: false);
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot));
        AssertColor(BaseColor, Color(Plain()), "no ArmedColor");

        // Cleared (profile load) while armed: base at once.
        Publish(Slot, Macros(armedColor: ArmedColor), L2Macros());
        Frame(Input(), 1300, custom: false);
        Frame(Toggle(), 1301, custom: false);
        AssertColor(ArmedColor, Color(Plain()), "armed");
        AimLayerState.Clear(Slot);
        AssertColor(BaseColor, Color(Plain()), "cleared");
    }

    // ---- Allocation -----------------------------------------------------------

    [TestMethod]
    public void PerReportStepDoesNotAllocate()
    {
        var input = Input(rx: 150, l2: 255, r2: 255);
        var state = new DS4State(input);

        long Measure(OutContType output)
        {
            for (int warm = 0; warm < 1000; warm++)
                ControlService.ApplyAimMacros(Slot, input, state, scratch, 10_000 + warm, output);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int report = 0; report < 20_000; report++)
                ControlService.ApplyAimMacros(Slot, input, state, scratch, 20_000 + report, output);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.AreEqual(0, Measure(OutContType.ViiperDS4), "nothing published");

        Publish(Slot, Macros(), L2Macros());
        Mapping.SetCurveAndDeadzone(Slot, input, temp, owner, 5_000); // L2 layer active
        Assert.AreEqual(0, Measure(OutContType.ViiperDS4), "disarmed");

        Step(Slot, Toggle(l2: 255), 5_001);
        Assert.IsTrue(AimMacroRuntime.IsArmed(Slot));
        Assert.IsNotNull(AimLayerState.Active(Slot));
        Assert.AreEqual(0, Measure(OutContType.ViiperDS4), "armed, byte output");
        Assert.AreEqual(0, Measure(OutContType.ViiperX360), "armed, high-res output");
    }

    // ---- Wiring in the report loop -------------------------------------------

    [TestMethod]
    public void ReportLoopRunsTheHookAfterMappingAndBeforeOutput()
    {
        string source = ControlServiceSource();
        string report = Between(source, "private void OnReportCore(",
            "internal static DS4State ApplyAimMacros(");

        int input = At(report, "DS4State aimMacroInput = cState;");
        int debounce = At(report, "cState = device.Debouncer.ProcessInput(cState);");
        int curve = At(report, "cState = Mapping.SetCurveAndDeadzone(ind, cState, TempState[ind], device,\n                    reportNowMs);");
        int custom = At(report, "Mapping.MapCustom(ind, cState, tempMapState");
        int plain = At(report, "Mapping.ResetFlickStickCalibration(ind);\n                }");
        int hook = At(report, "cState = ApplyAimMacros(ind, aimMacroInput, cState,\n                    AimMacroOutputState[ind], reportNowMs, activeOutDevType[ind]);");
        int send = At(report, "reportOutput?.ConvertandSendReport(cState, ind);");
        int joined = At(report, "Mapping.TempMouseJoystick(jointInd, tempMapState);");
        int skip = At(report, "// Skip mapping routine if part of a joined device");

        Assert.IsTrue(debounce < input && input < curve, "the hook reads the unmapped report");
        Assert.IsTrue(curve < custom && custom < plain && plain < hook && hook < send,
            "one hook after both mapping paths and before the send");
        Assert.IsTrue(joined < skip && skip < hook, "joined devices return before the hook");
        Assert.AreEqual(1, Count(report, "ApplyAimMacros("), "exactly one hook");
        Assert.AreEqual(1, Count(report, "Mapping.ReportNowMs()"), "one clock read per report");

        string hookBody = Between(source, "internal static DS4State ApplyAimMacros(",
            "internal static void CaptureReportBatteryDiagnostic(");
        StringAssert.Contains(hookBody,
            "AimMacroSettings macros = AimLayerState.Current(ind)?.Macros;\n            if (macros == null)\n                return mapped;");
    }

    // ---- Helpers --------------------------------------------------------------

    private void WaitForLogs(int count)
    {
        Assert.IsTrue(SpinWait.SpinUntil(() => { lock (logs) return logs.Count >= count; }, 5000),
            $"expected {count} log lines, got: {string.Join(" | ", Logs())}");
        // Let a stray extra line arrive, so an exact comparison can catch it.
        Thread.Sleep(50);
    }

    private string[] Logs()
    {
        lock (logs) return logs.ToArray();
    }

    private static string ControlServiceSource([CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(testPath), "..", "DS4Windows",
            "DS4Control", "ControlService.cs")).Replace("\r\n", "\n");

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && last > first, $"{start} .. {end}");
        return source[first..last];
    }

    private static int At(string text, string part)
    {
        int at = text.IndexOf(part, StringComparison.Ordinal);
        Assert.IsTrue(at >= 0, part);
        return at;
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
