using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;
using Sensorit.Base;

namespace DS4WindowsTests;

/// <summary>
/// Task 6.1: GyroMouseStickMath must give the same bytes as the frozen copy
/// of Mouse.SixMouseStickCore (LegacyGyroMouseStickOracle), report by report,
/// with the smoothing ring and One Euro filters kept in step. Pure maths: no
/// live store, except the last test, which drives Mouse itself on a private
/// store to check the wiring.
/// </summary>
[TestClass]
public class GyroMouseStickMathTests
{
    private const int Seed = 6100;

    private static readonly int[] GridGyro =
    {
        -2000, -1200, -831, -830, -317, -64, -31, -30, -29, -7, -3, -2, -1,
        0, 1, 2, 3, 7, 29, 30, 31, 64, 317, 830, 831, 1200, 2000,
    };

    private static readonly Switch2GyroTriggerModifierResult NoModifier = default;
    private static readonly Switch2GyroTriggerModifierResult DeadzoneOnly =
        new(true, false, true, 12.5, false, 1.0);
    private static readonly Switch2GyroTriggerModifierResult DampenOnly =
        new(true, false, false, 0.0, true, 0.35);
    private static readonly Switch2GyroTriggerModifierResult BothModifiers =
        new(true, false, true, 40.0, true, 0.6);

    [TestMethod]
    public void GridMatchesOracleForEveryOptionCombination()
    {
        var methods = new[]
        {
            (false, GyroMouseStickInfo.SmoothingMethod.None),
            (true, GyroMouseStickInfo.SmoothingMethod.None),
            (true, GyroMouseStickInfo.SmoothingMethod.OneEuro),
            (true, GyroMouseStickInfo.SmoothingMethod.WeightedAverage),
            // Method set but smoothing off: the filters must stay untouched.
            (false, GyroMouseStickInfo.SmoothingMethod.OneEuro),
        };
        int[] vertScales = { 100, 45, 180 };
        var modifiers = new[] { NoModifier, BothModifiers };
        double[] elapsed = { 0.001, 0.004, 0.008 };
        long reports = 0;
        int config = 0;

        foreach (var (useSmoothing, method) in methods)
        foreach (bool jitter in new[] { false, true })
        foreach (int vertScale in vertScales)
        foreach (bool maxOut in new[] { false, true })
        foreach (uint inverted in new uint[] { 0, 1, 2, 3 })
        foreach (var modifier in modifiers)
        {
            var settings = new GyroMouseStickInfo
            {
                useSmoothing = useSmoothing,
                smoothingMethod = method,
                jitterCompensation = jitter,
                vertScale = vertScale,
                maxOutputEnabled = maxOut,
                maxOutput = 62.5,
                inverted = inverted,
                smoothWeight = 0.35,
            };
            var pair = new Pair(settings.minCutoff, settings.beta);
            int cell = 0;
            foreach (int yaw in GridGyro)
            foreach (int pitch in GridGyro)
            {
                // Roll and the axis choice walk the grid too, so both yaw and
                // roll reach the horizontal output.
                int horizontal = (config + cell) & 1;
                int rollValue = GridGyro[(cell * 7 + config) % GridGyro.Length];
                cell++;
                double dt = elapsed[(reports & 0xff) % elapsed.Length];
                pair.Step(yaw, pitch, rollValue, dt, horizontal, settings, modifier,
                    active: true, $"config {config}, yaw {yaw}, pitch {pitch}");
                reports++;
            }
            pair.AssertStateEqual($"config {config}");
            config++;
        }

        Assert.IsTrue(reports >= 100_000, $"only {reports} reports");
    }

    [TestMethod]
    public void RandomSequencesMatchOracleIncludingFilterState()
    {
        var rng = new Random(Seed);
        long reports = 0, resets = 0;
        for (int sequence = 0; sequence < 250; sequence++)
        {
            GyroMouseStickInfo settings = RandomSettings(rng);
            var pair = new Pair(settings.minCutoff, settings.beta);
            int horizontal = rng.Next(2);
            var modifier = RandomModifier(rng);
            int yaw = 0, pitch = 0, roll = 0;
            for (int i = 0; i < 600; i++)
            {
                // Mostly smooth motion (as a hand makes), with jumps.
                if (rng.Next(20) == 0)
                {
                    yaw = rng.Next(-2000, 2001);
                    pitch = rng.Next(-2000, 2001);
                    roll = rng.Next(-2000, 2001);
                }
                else
                {
                    yaw = Math.Clamp(yaw + rng.Next(-60, 61), -2000, 2000);
                    pitch = Math.Clamp(pitch + rng.Next(-60, 61), -2000, 2000);
                    roll = Math.Clamp(roll + rng.Next(-60, 61), -2000, 2000);
                }
                // Occasional out-of-range spikes (full 16-bit range).
                if (rng.Next(500) == 0) yaw = rng.Next(-32768, 32768);

                double dt = 0.001 + rng.NextDouble() * 0.007;
                // Trigger released for a stretch now and then: reset path.
                bool active = (i / 50) % 4 != 3 || rng.Next(8) != 0;
                if (rng.Next(200) == 0) modifier = RandomModifier(rng);
                // Settings edited mid-hold (the editor can do this live).
                if (rng.Next(300) == 0) settings.smoothingMethod =
                    (GyroMouseStickInfo.SmoothingMethod)rng.Next(3);

                pair.Step(yaw, pitch, roll, dt, horizontal, settings, modifier, active,
                    $"sequence {sequence}, report {i}");
                pair.AssertStateEqual($"sequence {sequence}, report {i}");
                reports++;
                if (!active) resets++;
            }
        }

        Assert.IsTrue(reports >= 100_000, $"only {reports} reports");
        Assert.IsTrue(resets > 1000, $"only {resets} resets");
    }

    [TestMethod]
    public void EverySmoothingMethodMatchesOverLongSequences()
    {
        var rng = new Random(Seed + 1);
        foreach (var method in Enum.GetValues<GyroMouseStickInfo.SmoothingMethod>())
        foreach (double weight in new[] { 0.0, 0.2, 0.5, 0.9, 1.0 })
        {
            var settings = new GyroMouseStickInfo
            {
                useSmoothing = true,
                smoothingMethod = method,
                smoothWeight = weight,
                minCutoff = 0.1 + weight,
                beta = 2.0 * weight,
                jitterCompensation = weight > 0.4,
            };
            var pair = new Pair(settings.minCutoff, settings.beta);
            for (int i = 0; i < 4000; i++)
            {
                // Slow sine sweep with noise, through the deadzone and back.
                int yaw = (int)(900 * Math.Sin(i * 0.013)) + rng.Next(-8, 9);
                int pitch = (int)(400 * Math.Cos(i * 0.021)) + rng.Next(-8, 9);
                pair.Step(yaw, pitch, -yaw, 0.001 * (1 + i % 8), i % 2, settings,
                    NoModifier, active: i % 500 < 450, $"{method} w {weight} report {i}");
            }
            pair.AssertStateEqual($"{method} w {weight}");
        }
    }

    [TestMethod]
    public void DefaultsGiveKnownBytes()
    {
        // Spot values with the profile defaults (deadzone 30, maxZone 830,
        // anti-deadzone 0.4), so the oracle tests can't pass on two copies of
        // the same mistake.
        var settings = new GyroMouseStickInfo();
        var state = NewState(settings);
        Assert.AreEqual((byte)128, Compute(0, 0, settings, ref state).AxisX);
        Assert.AreEqual((byte)128, Compute(30, 0, settings, ref state).AxisX);
        // Just past the deadzone: anti-deadzone jumps to ~40%.
        Assert.AreEqual((byte)(((1.0 - 0.4) * (1.0 / 830) + 0.4) * 127 + 128.0),
            Compute(31, 0, settings, ref state).AxisX);
        Assert.AreEqual((byte)255, Compute(2000, 0, settings, ref state).AxisX);
        Assert.AreEqual((byte)0, Compute(-2000, 0, settings, ref state).AxisX);
        // Pitch up (positive counts) is stick up (low Y byte).
        Assert.AreEqual((byte)0, Compute(0, 2000, settings, ref state).AxisY);
    }

    [TestMethod]
    public void WarmComputeAndResetAllocateNothing()
    {
        var oneEuro = new GyroMouseStickInfo
        {
            useSmoothing = true,
            smoothingMethod = GyroMouseStickInfo.SmoothingMethod.OneEuro,
            jitterCompensation = true,
            vertScale = 80,
            maxOutputEnabled = true,
            maxOutput = 90,
            inverted = 3,
        };
        var weighted = new GyroMouseStickInfo
        {
            useSmoothing = true,
            smoothingMethod = GyroMouseStickInfo.SmoothingMethod.WeightedAverage,
        };
        var stateA = NewState(oneEuro);
        var stateB = NewState(weighted);
        int sink = 0;

        for (int i = 0; i < 2000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.AreNotEqual(0, sink);

        void Step(int i)
        {
            int yaw = (i * 37) % 4001 - 2000, pitch = (i * 53) % 4001 - 2000;
            var modifier = (i & 4) == 0 ? NoModifier : BothModifiers;
            if (i % 100 < 90)
            {
                var a = GyroMouseStickMath.Compute(yaw, pitch, -yaw, 0.004, i & 1,
                    oneEuro, modifier, ref stateA);
                var b = GyroMouseStickMath.Compute(yaw, pitch, -yaw, 0.002, i & 1,
                    weighted, modifier, ref stateB);
                sink += a.AxisX + b.AxisY;
            }
            else
            {
                GyroMouseStickMath.Reset(0.004, oneEuro, ref stateA);
                GyroMouseStickMath.Reset(0.002, weighted, ref stateB);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void MouseSubmitsTheOracleBytes()
    {
        // Mouse.SixMouseStick/SixMouseReset on a private store, so the reading
        // of globals (axis choice, pitch sign, filters) is pinned too.
        const int Slot = 1;
        FieldInfo storeField = typeof(Global).GetField("m_Config",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        BackingStore previousStore = Global.store;
        Mapping.PostMapStickData previousData = Mapping.mapStickActionData[Slot];
        byte previousX = Mapping.gyroStickX[Slot], previousY = Mapping.gyroStickY[Slot];
        try
        {
            storeField.SetValue(null, new BackingStore());
            var data = Mapping.mapStickActionData[Slot] = new Mapping.PostMapStickData();
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1, Switch2Transport.Usb,
                out var runtime, out _));
            var mouse = new Mouse(Slot, runtime);
            Global.GyroOutputMode[Slot] = GyroOutMode.MouseJoystick;
            GyroMouseStickInfo settings = Global.GetGyroMouseStickInfo(Slot);
            settings.outputStick = GyroMouseStickInfo.OutputStick.RightStick;
            settings.outputStickDir = GyroMouseStickInfo.OutputStickAxes.XY;
            settings.useSmoothing = true;
            settings.jitterCompensation = true;
            settings.vertScale = 120;
            var stick = typeof(Mouse).GetMethod("SixMouseStick",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var reset = typeof(Mouse).GetMethod("SixMouseReset",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            // Mouse's filters start at the class defaults, like the oracle's.
            var oracle = new LegacyGyroMouseStickOracle(
                GyroMouseStickInfo.DEFAULT_MINCUTOFF, GyroMouseStickInfo.DEFAULT_BETA);
            var rng = new Random(Seed + 2);
            var sixAxis = new SixAxis(0, 0, 0, 0, 0, 0, 0.004);
            var args = new SixAxisEventArgs(DateTime.UnixEpoch, sixAxis);
            object[] stickArgs = { args, NoModifier };
            object[] resetArgs = { args };

            for (int i = 0; i < 3000; i++)
            {
                if (i % 1000 == 0)
                {
                    settings.smoothingMethod = (GyroMouseStickInfo.SmoothingMethod)(i / 1000);
                }
                Global.GyroMouseStickHorizontalAxis[Slot] = (i / 250) & 1;
                sixAxis.gyroYawFull = rng.Next(-2000, 2001);
                sixAxis.gyroPitchFull = rng.Next(-2000, 2001);
                sixAxis.gyroRollFull = rng.Next(-2000, 2001);
                sixAxis.elapsed = 0.001 + rng.NextDouble() * 0.007;
                if (i % 100 >= 90)
                {
                    reset.Invoke(mouse, resetArgs);
                    oracle.SixMouseReset(args, settings);
                    continue;
                }

                data.Reset();
                stick.Invoke(mouse, stickArgs);
                oracle.SixMouseStickCore(args, NoModifier,
                    Global.GyroMouseStickHorizontalAxis[Slot], settings,
                    out byte x, out byte y);
                Assert.AreEqual(x, data.RX, $"report {i} X");
                Assert.AreEqual(y, data.RY, $"report {i} Y");
            }
        }
        finally
        {
            Mapping.mapStickActionData[Slot] = previousData;
            Mapping.gyroStickX[Slot] = previousX;
            Mapping.gyroStickY[Slot] = previousY;
            storeField.SetValue(null, previousStore);
        }
    }

    private static GyroMouseStickOutput Compute(int yaw, int pitch,
        GyroMouseStickInfo settings, ref GyroMouseStickFilterState state) =>
        GyroMouseStickMath.Compute(yaw, pitch, 0, 0.004, 0, settings, NoModifier,
            ref state);

    private static GyroMouseStickFilterState NewState(GyroMouseStickInfo settings) =>
        new(new OneEuroFilter(settings.minCutoff, settings.beta),
            new OneEuroFilter(settings.minCutoff, settings.beta));

    private static GyroMouseStickInfo RandomSettings(Random rng)
    {
        var settings = new GyroMouseStickInfo
        {
            deadZone = rng.Next(4) == 0 ? 0 : rng.Next(0, 120),
            maxZone = rng.Next(6) == 0 ? rng.Next(1, 40) : rng.Next(100, 2200),
            antiDeadX = rng.Next(4) == 0 ? 0.0 : rng.NextDouble() * 0.9,
            antiDeadY = rng.Next(4) == 0 ? 0.0 : rng.NextDouble() * 0.9,
            vertScale = rng.Next(3) == 0 ? 100 : rng.Next(10, 300),
            maxOutputEnabled = rng.Next(3) == 0,
            maxOutput = rng.NextDouble() * 100.0,
            inverted = (uint)rng.Next(4),
            useSmoothing = rng.Next(4) != 0,
            smoothingMethod = (GyroMouseStickInfo.SmoothingMethod)rng.Next(3),
            smoothWeight = rng.NextDouble(),
            minCutoff = 0.05 + rng.NextDouble() * 3.0,
            beta = rng.NextDouble() * 2.0,
            jitterCompensation = rng.Next(2) == 0,
        };
        return settings;
    }

    private static Switch2GyroTriggerModifierResult RandomModifier(Random rng) =>
        rng.Next(4) switch
        {
            0 => NoModifier,
            1 => DeadzoneOnly,
            2 => DampenOnly,
            _ => new(true, false, rng.Next(2) == 0, rng.NextDouble() * 120.0,
                rng.Next(2) == 0, rng.NextDouble() * 1.2),
        };

    /// <summary>The oracle and the new code fed the same reports.</summary>
    private sealed class Pair
    {
        private static readonly FieldInfo OneEuroFirst = typeof(OneEuroFilter).GetField(
            "firstTime", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo OneEuroX = typeof(OneEuroFilter).GetField(
            "xFilt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo OneEuroDx = typeof(OneEuroFilter).GetField(
            "dxFilt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo LowpassFirst = typeof(LowpassFilter).GetField(
            "firstTime", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly LegacyGyroMouseStickOracle oracle;
        private readonly SixAxis sixAxis = new(0, 0, 0, 0, 0, 0, 0.004);
        private readonly SixAxisEventArgs args;
        private GyroMouseStickFilterState state;

        internal Pair(double minCutoff, double beta)
        {
            oracle = new LegacyGyroMouseStickOracle(minCutoff, beta);
            args = new SixAxisEventArgs(DateTime.UnixEpoch, sixAxis);
            state = new GyroMouseStickFilterState(new OneEuroFilter(minCutoff, beta),
                new OneEuroFilter(minCutoff, beta));
        }

        internal void Step(int yaw, int pitch, int roll, double elapsed,
            int horizontal, GyroMouseStickInfo settings,
            in Switch2GyroTriggerModifierResult modifier, bool active, string where)
        {
            sixAxis.gyroYawFull = yaw;
            sixAxis.gyroPitchFull = pitch;
            sixAxis.gyroRollFull = roll;
            sixAxis.elapsed = elapsed;
            if (!active)
            {
                oracle.SixMouseReset(args, settings);
                GyroMouseStickMath.Reset(elapsed, settings, ref state);
                return;
            }

            oracle.SixMouseStickCore(args, modifier, horizontal, settings,
                out byte x, out byte y);
            GyroMouseStickOutput output = GyroMouseStickMath.Compute(yaw, pitch, roll,
                elapsed, horizontal, settings, modifier, ref state);
            if (x != output.AxisX || y != output.AxisY)
            {
                Assert.Fail($"{where}: oracle ({x}, {y}) vs new ({output.AxisX}, {output.AxisY})");
            }
        }

        internal void AssertStateEqual(string where)
        {
            Assert.AreEqual(oracle.smoothBufferTail, state.SmoothTail, $"{where}: tail");
            for (int i = 0; i < GyroMouseStickFilterState.SmoothBufferLength; i++)
            {
                if (oracle.xSmoothBuffer[i] != state.SmoothX[i] ||
                    oracle.ySmoothBuffer[i] != state.SmoothY[i])
                {
                    Assert.Fail($"{where}: smoothing buffer [{i}]");
                }
            }
            AssertFilterEqual(oracle.filterPair.axis1Filter, state.FilterX, where + " X");
            AssertFilterEqual(oracle.filterPair.axis2Filter, state.FilterY, where + " Y");
        }

        private static void AssertFilterEqual(OneEuroFilter expected, OneEuroFilter actual,
            string where)
        {
            if (!OneEuroFirst.GetValue(expected)!.Equals(OneEuroFirst.GetValue(actual)) ||
                !LowpassEqual((LowpassFilter)OneEuroX.GetValue(expected)!,
                    (LowpassFilter)OneEuroX.GetValue(actual)!) ||
                !LowpassEqual((LowpassFilter)OneEuroDx.GetValue(expected)!,
                    (LowpassFilter)OneEuroDx.GetValue(actual)!))
            {
                Assert.Fail($"{where}: One Euro filter state differs");
            }
        }

        private static bool LowpassEqual(LowpassFilter a, LowpassFilter b) =>
            LowpassFirst.GetValue(a)!.Equals(LowpassFirst.GetValue(b)) &&
            BitConverter.DoubleToInt64Bits(a.Last()) == BitConverter.DoubleToInt64Bits(b.Last());
    }
}
