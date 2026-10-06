using System.Reflection;
using DS4Windows;
using DS4Windows.DS4Control;

namespace DS4WindowsTests;

/// <summary>
/// Aim layer selection with hold delays (Task 5.2), report by report on a
/// fixed clock: every layer times its own raw trigger, the first ready layer
/// in file order wins, and a new publish restarts the timers. The user's
/// setup: L2 -> Expo (no delay) first, then R2 -> Hipfire after 100 ms.
/// Uses a private live store; never the real one.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimLayerSelectionTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;
    private const int Delay = 100;
    // Arbitrary report time the sequences start from.
    private const long T0 = 50_000;

    private static readonly FieldInfo StoreField =
        typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);

    private object oldStore;
    private readonly object owner = new();
    private AimLayerStickSettings expo;
    private AimLayerStickSettings hipfire;

    [TestInitialize]
    public void Setup()
    {
        oldStore = StoreField.GetValue(null);
        StoreField.SetValue(null, BaseStore());
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
    }

    private static BezierCurve Curve(double a, double b)
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(a, b, 1.00, 1.00, BezierCurve.AxisType.LSRS));
        return curve;
    }

    // Edge Linear-like base. Its L2/R2 dead zones and sensitivity change the
    // processed trigger values a lot, so a decision taken from processed
    // instead of raw triggers lands on the wrong side of the thresholds.
    private static BackingStore BaseStore()
    {
        var store = new BackingStore();
        TriggerDeadZoneZInfo l2 = store.l2ModInfo[Slot];
        l2.deadZone = 30; l2.antiDeadZone = 10; l2.maxZone = 90;
        store.l2Sens[Slot] = 1.1;
        TriggerDeadZoneZInfo r2 = store.r2ModInfo[Slot];
        r2.deadZone = 20; r2.antiDeadZone = 15; r2.maxZone = 80;
        return store;
    }

    private static AimLayerStickSettings Layer(DS4Controls trigger, byte threshold, int delay,
        int index, int mode, BezierCurve curve, string source)
    {
        var request = new AimLayerRequest("Edge Linear", source, trigger, threshold, true,
            delay, index);
        return new AimLayerStickSettings(request, new StickDeadZoneInfo(), 1.0, false,
            SquareStickInfo.DEFAULT_ROUNDNESS, mode, curve, new DS4Color(0, 0, 0));
    }

    // Publishes L2 -> Expo then R2 -> Hipfire, as a profile apply does.
    private void PublishUserSetup(byte l2Threshold = 100, byte r2Threshold = 100,
        int expoDelay = 0, int hipfireDelay = Delay)
    {
        expo = Layer(DS4Controls.L2, l2Threshold, expoDelay, 0, 6, Curve(0.72, 0.26), "Edge Expo");
        hipfire = Layer(DS4Controls.R2, r2Threshold, hipfireDelay, 1, 6, Curve(0.3, 0.11),
            "Edge Hipfire");
        Republish();
    }

    // A new preparation of the same layers (profile applied again).
    private void Republish()
    {
        var requests = new[]
        {
            new AimLayerRequest("Edge Linear", "Edge Expo", expo.Trigger, expo.Threshold, true,
                expo.Delay, 0),
            new AimLayerRequest("Edge Linear", "Edge Hipfire", hipfire.Trigger, hipfire.Threshold,
                true, hipfire.Delay, 1),
        };
        // Same save sequence: no background rebuild (nothing to read).
        AimLayerState.Publish(Slot, new AimLayerPreparation(requests,
            new[] { expo, hipfire }, AimLayerState.ReadSaveSequence()));
        Assert.AreEqual(2, AimLayerState.Current(Slot).Count);
    }

    private static DS4State Input(byte l2, byte r2, byte rx = 190, byte ry = 70) =>
        new() { LX = 128, LY = 128, RX = rx, RY = ry, L2 = l2, R2 = r2 };

    // One report at time nowMs; returns the active layer (null = base).
    private AimLayerStickSettings Report(long nowMs, byte l2, byte r2)
    {
        Mapping.SetCurveAndDeadzone(Slot, Input(l2, r2), new DS4State(), owner, nowMs);
        return AimLayerState.Active(Slot);
    }

    private static string Name(AimLayerStickSettings layer) =>
        layer == null ? "base" : layer.SourceProfile;

    // (ms after T0, L2, R2, expected layer: null = base).
    private void Expect(params (long ms, byte l2, byte r2, AimLayerStickSettings layer)[] steps)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            AimLayerStickSettings actual = Report(T0 + step.ms, step.l2, step.r2);
            Assert.AreSame(step.layer, actual,
                $"step {i} at +{step.ms} ms, L2 {step.l2}, R2 {step.r2}: " +
                $"expected {Name(step.layer)}, got {Name(actual)}");
        }
    }

    [TestMethod]
    public void R2OnlyWaitsTheFullDelayAndReleaseIsImmediate()
    {
        PublishUserSetup();
        Expect(
            (0, 0, 0, null),
            (1, 0, 255, null),          // press: R2's timer starts here
            (50, 0, 255, null),
            (1 + Delay - 1, 0, 255, null),
            (1 + Delay, 0, 255, hipfire),
            (300, 0, 200, hipfire),
            (301, 0, 0, null),          // release: base on that report
            (302, 0, 255, null),        // re-press waits the full delay again
            (302 + Delay - 1, 0, 255, null),
            (302 + Delay, 0, 255, hipfire),
            (500, 0, 101, hipfire),
            (501, 0, 100, null),        // a dip to the threshold is a release
            (502, 0, 101, null),
            (502 + Delay - 1, 0, 101, null),
            (502 + Delay, 0, 101, hipfire));
    }

    [TestMethod]
    public void DelayZeroLayerIsReadyOnThePressReport()
    {
        PublishUserSetup(hipfireDelay: 0);
        Expect(
            (0, 0, 0, null),
            (1, 0, 255, hipfire),
            (2, 0, 0, null),
            (3, 0, 255, hipfire),
            (4, 255, 255, expo),
            (5, 0, 255, hipfire));
    }

    [TestMethod]
    public void L2WinsAtAnyTimeIncludingWhileHipfireIsActive()
    {
        PublishUserSetup();
        Expect(
            (0, 255, 0, expo),          // L2 alone: Expo on the press report
            (1, 0, 0, null),
            (10, 0, 255, null),
            (10 + Delay, 0, 255, hipfire),
            (200, 101, 255, expo),      // L2 pressed while Hipfire is active
            (250, 255, 255, expo),
            (251, 101, 0, expo),
            (252, 0, 0, null),
            (253, 255, 0, expo),
            (254, 255, 255, expo),      // R2 pressed while L2 held: still Expo
            (254 + 500, 255, 255, expo));
    }

    [TestMethod]
    public void L2ReleasedAfterR2PassedTheDelaySwitchesStraightToHipfire()
    {
        PublishUserSetup();
        Expect(
            (0, 255, 0, expo),
            (10, 255, 255, expo),       // R2's timer runs under L2
            (10 + Delay, 255, 255, expo),
            (200, 255, 255, expo),
            (201, 0, 255, hipfire),     // L2 released: Hipfire on that report
            (250, 0, 255, hipfire),
            (251, 0, 0, null));

        // R2 already held 100 ms when L2 comes and goes.
        AimLayerState.ResetSelectionForTests(Slot);
        Expect(
            (1000, 0, 255, null),
            (1000 + Delay, 0, 255, hipfire),
            (1150, 255, 255, expo),
            (1151, 0, 255, hipfire));   // not restarted by L2's press or release
    }

    [TestMethod]
    public void L2ReleasedBeforeR2ReachesTheDelayGivesBaseUntilItDoes()
    {
        PublishUserSetup();
        Expect(
            (0, 255, 0, expo),
            (5, 255, 255, expo),        // R2 pressed while L2 is held
            (35, 0, 255, null),         // L2 released 30 ms in: base
            (60, 0, 255, null),
            (5 + Delay - 1, 0, 255, null),
            (5 + Delay, 0, 255, hipfire));
    }

    [TestMethod]
    public void ThresholdBoundariesForBothLayers()
    {
        foreach ((byte l2t, byte r2t) in new (byte, byte)[] { (100, 100), (60, 40), (180, 200) })
        {
            AimLayerState.ResetSelectionForTests(Slot);
            PublishUserSetup(l2t, r2t);
            byte l2At = l2t, l2Above = (byte)(l2t + 1);
            byte r2At = r2t, r2Above = (byte)(r2t + 1);
            string context = $"thresholds L2 {l2t}, R2 {r2t}";
            try
            {
                Expect(
                    (0, l2At, 0, null),     // == Threshold: not held
                    (1, l2Above, 0, expo),  // Threshold + 1: held
                    (2, l2At, 0, null),
                    (3, 0, r2At, null),
                    (3 + Delay * 3, 0, r2At, null), // never starts at the threshold
                    (400, 0, r2Above, null),        // R2's timer starts here
                    (400 + Delay - 1, 0, r2Above, null),
                    (400 + Delay, 0, r2Above, hipfire),
                    (600, l2At, r2Above, hipfire),
                    (601, l2Above, r2Above, expo),
                    (602, l2At, r2Above, hipfire),
                    (603, l2At, r2At, null));
            }
            catch (AssertFailedException ex)
            {
                throw new AssertFailedException($"{context}: {ex.Message}", ex);
            }
        }
    }

    [TestMethod]
    public void NewPublishRestartsTimersFromTheFirstReportAfterIt()
    {
        PublishUserSetup();
        Expect(
            (0, 0, 255, null),
            (Delay, 0, 255, hipfire),
            (150, 0, 255, hipfire));

        // Profile applied again while R2 stays held; the first report after
        // it is much later than the apply.
        Republish();
        Assert.IsNull(AimLayerState.Active(Slot), "A publish clears the active layer.");
        Expect(
            (400, 0, 255, null),        // timer starts on this report
            (400 + Delay - 1, 0, 255, null),
            (400 + Delay, 0, 255, hipfire),
            // L2 still wins at once after a publish (no delay on it).
            (600, 255, 255, expo));

        Republish();
        Expect(
            (601, 255, 255, expo),      // R2 under L2 also restarted at 601
            (602, 0, 255, null),
            (601 + Delay - 1, 0, 255, null),
            (601 + Delay, 0, 255, hipfire));
    }

    [TestMethod]
    public void ClearSuspendAndResumeRestartTimers()
    {
        PublishUserSetup();
        Expect((0, 0, 255, null), (Delay, 0, 255, hipfire));

        // Controller removed and reconnected with the same profile.
        AimLayerState.Suspend(Slot);
        Assert.IsNull(Report(T0 + 150, 0, 255), "No layer while suspended.");
        AimLayerState.Resume(Slot);
        Expect(
            (200, 0, 255, null),
            (200 + Delay - 1, 0, 255, null),
            (200 + Delay, 0, 255, hipfire));

        // Resume with no report while suspended.
        AimLayerState.Suspend(Slot);
        AimLayerState.Resume(Slot);
        Expect(
            (400, 0, 255, null),
            (400 + Delay, 0, 255, hipfire));

        AimLayerState.Clear(Slot);
        Assert.IsNull(Report(T0 + 600, 0, 255));
        Republish();
        Expect(
            (601, 0, 255, null),
            (601 + Delay, 0, 255, hipfire));
    }

    [TestMethod]
    public void StickOutputFollowsTheActiveLayer()
    {
        // References: base, Expo and Hipfire stick settings with no layer.
        DS4State Out(long nowMs, byte l2, byte r2)
        {
            var output = new DS4State();
            Mapping.SetCurveAndDeadzone(Slot, Input(l2, r2), output, owner, nowMs);
            return output;
        }

        var baseStore = (BackingStore)StoreField.GetValue(null);
        DS4State linear = Out(0, 0, 0);
        PublishUserSetup();
        AimLayerState.Clear(Slot);

        DS4State With(AimLayerStickSettings layer)
        {
            BackingStore store = BaseStore();
            store.rsModInfo[Slot] = new StickDeadZoneInfo();
            store.rsOutBezierCurveObj[Slot] = layer.RSOutBezierCurve;
            store.setRsOutCurveMode(Slot, layer.RSOutCurveMode);
            StoreField.SetValue(null, store);
            Mapping.ResetStickFilters(Slot);
            DS4State result = Out(0, 0, 0);
            StoreField.SetValue(null, baseStore);
            Mapping.ResetStickFilters(Slot);
            return result;
        }
        DS4State expoOut = With(expo), hipOut = With(hipfire);
        Assert.AreNotEqual(linear.RX, hipOut.RX);
        Assert.AreNotEqual(expoOut.RX, hipOut.RX);
        Assert.AreNotEqual(linear.RX, expoOut.RX);

        Republish();
        (long ms, byte l2, byte r2, DS4State expected)[] steps =
        {
            (0, 0, 0, linear), (1, 0, 255, linear), (Delay, 0, 255, linear),
            (Delay + 1, 0, 255, hipOut), (200, 255, 255, expoOut), (201, 0, 255, hipOut),
            (202, 0, 0, linear), (203, 255, 0, expoOut),
        };
        foreach (var step in steps)
        {
            DS4State actual = Out(T0 + step.ms, step.l2, step.r2);
            Assert.AreEqual(step.expected.RXAxis, actual.RXAxis, $"RX at +{step.ms}");
            Assert.AreEqual(step.expected.RYAxis, actual.RYAxis, $"RY at +{step.ms}");
        }
    }

    [TestMethod]
    public void PerReportPathAllocatesNothingWithTwoLayersInEveryState()
    {
        PublishUserSetup();
        var input = new DS4State();
        var output = new DS4State();
        int[] seen = new int[3];

        for (int i = 0; i < 2000; i++) Step(i);
        Array.Clear(seen);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        // Every state was visited in the measured calls.
        Assert.IsTrue(seen[0] > 0 && seen[1] > 0 && seen[2] > 0,
            $"base {seen[0]}, expo {seen[1]}, hipfire {seen[2]}");

        void Step(int i)
        {
            // 400-report cycle, 1 ms apart: idle, R2 (base then Hipfire),
            // L2 + R2 (Expo), R2 after L2 release (Hipfire), L2 only (Expo),
            // and short R2 taps.
            int phase = i % 400;
            byte l2 = 0, r2 = 0;
            if (phase >= 50 && phase < 200) r2 = 255;
            else if (phase >= 200 && phase < 300) { l2 = 200; r2 = 255; }
            else if (phase >= 300 && phase < 350) r2 = 180;
            else if (phase >= 350 && phase < 380) l2 = 255;
            else if (phase >= 380) r2 = (byte)((phase & 4) == 0 ? 255 : 0);
            input.RX = (byte)(i * 7); input.RY = (byte)(i * 13);
            input.L2 = l2; input.R2 = r2;
            Mapping.SetCurveAndDeadzone(Slot, input, output, owner, T0 + i);
            AimLayerStickSettings active = AimLayerState.Active(Slot);
            seen[active == null ? 0 : active == expo ? 1 : 2]++;
        }
    }
}
