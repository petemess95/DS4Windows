using System.Reflection;
using DS4Windows;
using DS4Windows.DS4Control;

namespace DS4WindowsTests;

/// <summary>
/// Aim layer runtime swap in Mapping.SetCurveAndDeadzone (Task 3.3): while
/// the trigger is held the right stick uses the borrowed settings, on the very
/// next report; everything else (and the no-layer path) is unchanged.
/// Uses a temp appdatapath and a private live store; never the real one.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimLayerMappingTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;

    private static readonly FieldInfo StoreField =
        typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);

    private string folder;
    private string oldAppDataPath;
    private object oldStore;
    private readonly object owner = new();

    // Right-stick settings that the aim layer can borrow.
    private sealed class Rs
    {
        internal Func<StickDeadZoneInfo> Mod = () => new StickDeadZoneInfo();
        internal double Sens = 1.0;
        internal bool Square;
        internal double Roundness = SquareStickInfo.DEFAULT_ROUNDNESS;
        internal int Mode;
        internal double[] Curve = { 0.72, 0.26, 1.00, 1.00 };
    }

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-aim-map-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Profiles"));
        oldAppDataPath = Global.appdatapath;
        Global.appdatapath = folder;
        oldStore = StoreField.GetValue(null);
        AimLayerState.Clear(Slot);
        Mapping.ResetStickFilters(Slot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        AimLayerState.Clear(Slot);
        StoreField.SetValue(null, oldStore);
        Mapping.ResetStickFilters(Slot);
        Global.appdatapath = oldAppDataPath;
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static Rs Linear() => new();

    // The user's Edge Expo right stick: custom 0.72, 0.26, 1.00, 1.00.
    private static Rs Expo() => new() { Mode = 6 };

    private static BezierCurve Curve(double[] c)
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(c[0], c[1], c[2], c[3], BezierCurve.AxisType.LSRS));
        return curve;
    }

    private static void ApplyRs(BackingStore store, Rs rs)
    {
        store.rsModInfo[Slot] = rs.Mod();
        store.RSSens[Slot] = rs.Sens;
        store.squStickInfo[Slot].rsMode = rs.Square;
        store.squStickInfo[Slot].rsRoundness = rs.Roundness;
        store.rsOutBezierCurveObj[Slot] = Curve(rs.Curve);
        store.setRsOutCurveMode(Slot, rs.Mode);
    }

    // Non-default left stick, triggers and base-only RS state, so the tests
    // can see that the layer leaves them alone.
    private static void ConfigureOthers(BackingStore store)
    {
        StickDeadZoneInfo ls = store.lsModInfo[Slot];
        ls.deadZone = 12; ls.antiDeadZone = 9; ls.maxZone = 92;
        store.LSSens[Slot] = 1.15;
        store.squStickInfo[Slot].lsMode = true;
        store.squStickInfo[Slot].lsRoundness = 2.5;
        store.lsOutBezierCurveObj[Slot] = Curve(new[] { 0.3, 0.1, 0.8, 0.9 });
        store.setLsOutCurveMode(Slot, 3);
        TriggerDeadZoneZInfo l2 = store.l2ModInfo[Slot];
        l2.deadZone = 30; l2.antiDeadZone = 10; l2.maxZone = 90;
        store.l2Sens[Slot] = 1.1;
        store.setL2OutCurveMode(Slot, 2);
        TriggerDeadZoneZInfo r2 = store.r2ModInfo[Slot];
        r2.deadZone = 20; r2.maxZone = 80; r2.maxOutput = 90;
        store.setR2OutCurveMode(Slot, 4);
    }

    private BackingStore Install(Rs rs, bool others = true, Action<BackingStore> extra = null)
    {
        var store = new BackingStore();
        if (others) ConfigureOthers(store);
        ApplyRs(store, rs);
        extra?.Invoke(store);
        StoreField.SetValue(null, store);
        Mapping.ResetStickFilters(Slot);
        return store;
    }

    private static AimLayerStickSettings Publish(Rs rs, DS4Controls trigger = DS4Controls.L2,
        byte threshold = 100)
    {
        var request = new AimLayerRequest("Base", "Source", trigger, threshold, false);
        var settings = new AimLayerStickSettings(request, rs.Mod(), rs.Sens, rs.Square,
            rs.Roundness, rs.Mode, Curve(rs.Curve), new DS4Color(255, 255, 255));
        // Same save sequence: no background rebuild (nothing to read).
        AimLayerState.Publish(Slot, new AimLayerPreparation(request, settings,
            AimLayerState.ReadSaveSequence()));
        Assert.AreEqual(1, AimLayerState.Current(Slot).Count);
        Assert.AreSame(settings, AimLayerState.Current(Slot)[0]);
        return settings;
    }

    private static DS4State Input(byte rx, byte ry, byte l2, byte r2, byte lx = 70, byte ly = 190) =>
        new() { LX = lx, LY = ly, RX = rx, RY = ry, L2 = l2, R2 = r2,
            OutputLSOuter = 41, OutputRSOuter = 97 };

    private DS4State Run(DS4State input)
    {
        var copy = new DS4State();
        input.CopyTo(copy);
        var output = new DS4State();
        Mapping.SetCurveAndDeadzone(Slot, copy, output, owner);
        return output;
    }

    private static void AssertSame(DS4State expected, DS4State actual, string context)
    {
        Assert.AreEqual(expected.LXAxis, actual.LXAxis, $"LX {context}");
        Assert.AreEqual(expected.LYAxis, actual.LYAxis, $"LY {context}");
        Assert.AreEqual(expected.RXAxis, actual.RXAxis, $"RX {context}");
        Assert.AreEqual(expected.RYAxis, actual.RYAxis, $"RY {context}");
        Assert.AreEqual(expected.L2, actual.L2, $"L2 {context}");
        Assert.AreEqual(expected.R2, actual.R2, $"R2 {context}");
        Assert.AreEqual(expected.OutputLSOuter, actual.OutputLSOuter, $"LS outer {context}");
        Assert.AreEqual(expected.OutputRSOuter, actual.OutputRSOuter, $"RS outer {context}");
    }

    // Expected outputs: the base profile with its RS settings replaced by
    // the source's, no layer.
    private DS4State[] Reference(Rs rs, DS4State[] inputs, bool others = true,
        Action<BackingStore> extra = null)
    {
        object live = StoreField.GetValue(null);
        AimLayerSet layer = AimLayerState.Current(Slot);
        Assert.IsNull(layer, "Compute references before publishing.");
        Install(rs, others, extra);
        var result = new DS4State[inputs.Length];
        for (int i = 0; i < inputs.Length; i++) result[i] = Run(inputs[i]);
        StoreField.SetValue(null, live);
        Mapping.ResetStickFilters(Slot);
        return result;
    }

    [TestMethod]
    public void L2AboveThresholdUsesSourceCurveOnTheVeryNextReport()
    {
        byte[] l2s = { 0, 101, 100, 255, 0, 255, 100, 101, 101, 0 };
        var inputs = new DS4State[l2s.Length];
        for (int i = 0; i < l2s.Length; i++)
            inputs[i] = Input((byte)(190 + i), (byte)(60 + i), l2s[i], 0);

        DS4State[] source = Reference(Expo(), inputs);
        Install(Linear());
        DS4State[] baseOut = new DS4State[inputs.Length];
        for (int i = 0; i < inputs.Length; i++) baseOut[i] = Run(inputs[i]);

        AimLayerStickSettings layer = Publish(Expo());
        StickDeadZoneInfo mod = layer.RSModInfo;
        byte[] lut = layer.RSOutBezierCurve.arrayBezierLUT.ToArray();
        for (int i = 0; i < inputs.Length; i++)
        {
            bool on = l2s[i] > 100;
            DS4State expected = on ? source[i] : baseOut[i];
            AssertSame(expected, Run(inputs[i]), $"report {i}, L2 {l2s[i]}");
            // The curves really differ here, so the swap is visible.
            Assert.AreNotEqual(baseOut[i].RX, source[i].RX, $"report {i}");
        }

        // Linear vs expo at a known point: expo is well below linear mid-stick.
        Assert.IsTrue(source[0].RX < baseOut[0].RX);
        // The shared borrowed objects were only read.
        Assert.AreSame(mod, layer.RSModInfo);
        Assert.AreEqual(StickDeadZoneInfo.DEFAULT_MAXZONE, mod.maxZone);
        CollectionAssert.AreEqual(lut, layer.RSOutBezierCurve.arrayBezierLUT);
    }

    [TestMethod]
    public void R2TriggerWithCustomThreshold()
    {
        (byte l2, byte r2, bool on)[] cases =
        {
            (0, 0, false), (0, 180, false), (0, 181, true), (255, 0, false),
            (255, 180, false), (0, 255, true), (200, 120, false), (0, 181, true),
        };
        var inputs = new DS4State[cases.Length];
        for (int i = 0; i < cases.Length; i++)
            inputs[i] = Input(220, 128, cases[i].l2, cases[i].r2);

        DS4State[] source = Reference(Expo(), inputs);
        Install(Linear());
        DS4State[] baseOut = new DS4State[inputs.Length];
        for (int i = 0; i < inputs.Length; i++) baseOut[i] = Run(inputs[i]);

        Publish(Expo(), DS4Controls.R2, 180);
        for (int i = 0; i < cases.Length; i++)
            AssertSame(cases[i].on ? source[i] : baseOut[i], Run(inputs[i]),
                $"case {i}: L2 {cases[i].l2}, R2 {cases[i].r2}");
    }

    [TestMethod]
    public void LeftStickAndTriggerOutputsIgnoreTheLayer()
    {
        Install(Linear());
        var inputs = new List<DS4State>();
        foreach (byte l2 in new byte[] { 101, 140, 200, 255 })
        foreach (byte r2 in new byte[] { 0, 50, 230 })
        foreach (byte lx in new byte[] { 0, 90, 128, 170, 255 })
            inputs.Add(Input(205, 75, l2, r2, lx, (byte)(255 - lx / 2)));
        var without = inputs.Select(Run).ToArray();

        Publish(Expo());
        bool anyRsDifferent = false;
        for (int i = 0; i < inputs.Count; i++)
        {
            DS4State with = Run(inputs[i]);
            Assert.AreEqual(without[i].LXAxis, with.LXAxis, $"LX {i}");
            Assert.AreEqual(without[i].LYAxis, with.LYAxis, $"LY {i}");
            Assert.AreEqual(without[i].OutputLSOuter, with.OutputLSOuter, $"LS outer {i}");
            // L2 is the aim trigger: the game still sees exactly the same L2.
            Assert.AreEqual(without[i].L2, with.L2, $"L2 {i}");
            Assert.AreEqual(without[i].R2, with.R2, $"R2 {i}");
            anyRsDifferent |= !without[i].RXAxis.Equals(with.RXAxis);
        }
        Assert.IsTrue(anyRsDifferent, "Layer should have changed the right stick.");
    }

    private static IEnumerable<Rs> Variants()
    {
        yield return Linear();
        yield return Expo();
        yield return new Rs
        {
            Mod = () => new StickDeadZoneInfo { deadZone = 15, antiDeadZone = 20, maxZone = 90 },
            Sens = 1.4, Square = true, Roundness = 3, Mode = 2,
        };
        yield return new Rs
        {
            Mod = () =>
            {
                var m = new StickDeadZoneInfo { deadzoneType = StickDeadZoneInfo.DeadZoneType.Axial };
                m.xAxisDeadInfo.deadZone = 9; m.xAxisDeadInfo.antiDeadZone = 14;
                m.yAxisDeadInfo.deadZone = 17; m.yAxisDeadInfo.maxZone = 85;
                m.yAxisDeadInfo.maxOutput = 92;
                return m;
            },
            Sens = 1.7, Square = true, Roundness = 5, Mode = 6, Curve = new[] { 0.23, 0.02, 0.82, 0.95 },
        };
        yield return new Rs
        {
            Mod = () => new StickDeadZoneInfo
            {
                deadZone = 5, maxOutput = 80, maxOutputForce = true, verticalScale = 120,
                outerBindDeadZone = 30, outerBindInvert = true,
            },
            Sens = 0.8, Mode = 1,
        };
        yield return new Rs
        {
            Mod = () => new StickDeadZoneInfo { deadzoneType = StickDeadZoneInfo.DeadZoneType.Axial },
            Square = true, Roundness = 1, Mode = 5,
        };
    }

    private static List<DS4State> Sweep()
    {
        var inputs = new List<DS4State>();
        byte[] sticks = { 0, 1, 40, 117, 127, 128, 129, 140, 200, 254, 255 };
        byte[] triggers = { 0, 100, 101, 255 };
        int n = 0;
        foreach (byte rx in sticks)
        foreach (byte ry in sticks)
        {
            byte l2 = triggers[n % triggers.Length];
            byte r2 = triggers[(n / triggers.Length) % triggers.Length];
            inputs.Add(Input(rx, ry, l2, r2, sticks[n % sticks.Length], ry));
            n++;
        }
        // High-resolution carriers too.
        foreach (double c in new[] { 3.25, 99.5, 127.75, 128.25, 171.125, 252.5 })
        {
            var s = Input(128, 128, (byte)(n % 2 == 0 ? 0 : 255), 30);
            Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(c, out var x));
            Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(255 - c, out var y));
            s.RXAxis = x; s.RYAxis = y; s.LXAxis = y;
            inputs.Add(s);
            n++;
        }
        return inputs;
    }

    [TestMethod]
    public void WithoutTheLayerOutputIsIdenticalAcrossSettingsAndInputs()
    {
        List<DS4State> inputs = Sweep();
        int variant = 0;
        foreach (Rs rs in Variants())
        {
            foreach (bool others in new[] { false, true })
            {
                Install(rs, others);
                AimLayerState.Clear(Slot);
                var cleared = inputs.Select(Run).ToArray();

                // A published layer whose trigger is never held (R2 > 255).
                Publish(Expo(), DS4Controls.R2, 255);
                for (int i = 0; i < inputs.Count; i++)
                    AssertSame(cleared[i], Run(inputs[i]), $"variant {variant}/{others}, input {i}");

                // Layer on with the base's own RS settings: also identical.
                Publish(rs, DS4Controls.L2, 0);
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (inputs[i].L2 == 0) continue;
                    AssertSame(cleared[i], Run(inputs[i]), $"self-layer variant {variant}/{others}, input {i}");
                }
                AimLayerState.Clear(Slot);
            }
            variant++;
        }
    }

    [TestMethod]
    public void LayerOnMatchesSourceSettingsForEveryVariant()
    {
        List<DS4State> inputs = Sweep();
        int variant = 0;
        foreach (Rs source in Variants())
        {
            DS4State[] expected = Reference(source, inputs.ToArray());
            Install(variant % 2 == 0 ? Expo() : Linear());
            Publish(source, DS4Controls.L2, 100);
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].L2 <= 100) continue;
                AssertSame(expected[i], Run(inputs[i]), $"variant {variant}, input {i}");
            }
            AimLayerState.Clear(Slot);
            variant++;
        }
    }

    [TestMethod]
    public void RotationCalibrationFuzzAndSnapbackStayOnTheBaseProfile()
    {
        // Base: rotation and calibration drift; source fuzz would differ.
        Action<BackingStore> baseOnly = s =>
        {
            s.RSRotation[Slot] = 0.35;
            s.rightStickDriftXAxis[Slot] = 6;
            s.rightStickDriftYAxis[Slot] = -4;
        };
        var inputs = new[] { Input(200, 90, 255, 0), Input(60, 210, 255, 0), Input(230, 230, 255, 0) };
        // Reference: base rotation/drift with the source RS settings.
        DS4State[] expected = Reference(Expo(), inputs, extra: baseOnly);
        Install(Linear(), extra: baseOnly);
        Publish(Expo());
        for (int i = 0; i < inputs.Length; i++)
            AssertSame(expected[i], Run(inputs[i]), $"rotation/drift input {i}");
        AimLayerState.Clear(Slot);

        // Fuzz: base 40, source 0. A 10-unit move is held by the base fuzz,
        // and toggling the layer between those reports does not reset it.
        Install(Linear(), extra: s => s.rsModInfo[Slot].fuzz = 40);
        Publish(Expo());
        DS4State first = Run(Input(200, 128, 255, 0));
        DS4State held = Run(Input(210, 128, 0, 0));    // layer off: base curve of 200
        DS4State heldOn = Run(Input(210, 128, 255, 0)); // layer on: source curve of 200
        AimLayerState.Clear(Slot);
        DS4State[] refs = Reference(Expo(), new[] { Input(200, 128, 255, 0), Input(210, 128, 255, 0) });
        Assert.AreEqual(refs[0].RXAxis, first.RXAxis);
        Assert.AreEqual(refs[0].RXAxis, heldOn.RXAxis, "Base fuzz held the 10-unit move.");
        Assert.AreNotEqual(refs[1].RXAxis, heldOn.RXAxis);
        Install(Linear());
        DS4State linear200 = Run(Input(200, 128, 0, 0));
        Assert.AreEqual(linear200.RXAxis, held.RXAxis);

        // Fuzz: base 0, source 60. Small moves pass straight through.
        Install(Linear());
        Rs bigFuzz = Expo();
        bigFuzz.Mod = () => new StickDeadZoneInfo { fuzz = 60 };
        DS4State[] noFuzz = Reference(Expo(), new[] { Input(170, 128, 255, 0), Input(180, 128, 255, 0) });
        Install(Linear());
        Publish(bigFuzz);
        Run(Input(170, 128, 255, 0));
        Assert.AreEqual(noFuzz[1].RXAxis, Run(Input(180, 128, 255, 0)).RXAxis,
            "The source's fuzz must not be used.");
        AimLayerState.Clear(Slot);

        // Anti-snapback (base only): a full flick through the centre is
        // suppressed to neutral even while the layer is on.
        Install(Linear(), extra: s =>
        {
            s.rsAntiSnapbackInfo[Slot].enabled = true;
            s.rsAntiSnapbackInfo[Slot].delta = 100;
            s.rsAntiSnapbackInfo[Slot].timeout = 1000;
        });
        Publish(Expo());
        Run(Input(255, 128, 255, 0));
        DS4State flick = Run(Input(0, 128, 255, 0));
        Assert.AreEqual((byte)128, flick.RX, "Base anti-snapback suppressed the flick.");
        AimLayerState.Clear(Slot);
        Install(Linear());
        Run(Input(255, 128, 255, 0));
        Assert.AreNotEqual((byte)128, Run(Input(0, 128, 255, 0)).RX, "Sanity: no snapback without it.");
    }

    [TestMethod]
    public void PerReportPathAllocatesNothingWithLayerOnOffOrAbsent()
    {
        Install(Linear());
        var input = new DS4State();
        var output = new DS4State();
        double checksum = 0;

        for (int i = 0; i < 2000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before, "No layer.");

        Publish(Expo());
        for (int i = 0; i < 2000; i++) Step(i);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before, "Layer published.");
        Assert.IsTrue(checksum > 0);

        void Step(int i)
        {
            input.RX = (byte)(i * 7); input.RY = (byte)(i * 13);
            input.LX = (byte)(i * 3); input.LY = (byte)(255 - i);
            // Alternate on/off every report, plus an R2 that never matters.
            input.L2 = (byte)((i & 1) == 0 ? 0 : 200);
            input.R2 = (byte)(i * 5);
            Mapping.SetCurveAndDeadzone(Slot, input, output, owner);
            checksum += output.RX + output.RY + output.LX + 1;
        }
    }
}
