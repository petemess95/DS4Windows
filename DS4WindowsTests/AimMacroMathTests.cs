using DS4Windows;

namespace DS4WindowsTests;

/// <summary>
/// Task 7.2: the pure aim-macro maths (recoil, circle, game-space dead zone,
/// clamp, dither). Units: stick deflection -1..1 with +Y = down (DS4 byte
/// 255 = down); macro fractions are of the game's live range.
/// </summary>
[TestClass]
public class AimMacroMathTests
{
    private const long T0 = 50_000;
    private const double Tight = 1e-9;
    private static readonly object LayerA = new();
    private static readonly object LayerB = new();

    private static AimMacroSettings Settings(double deadZone,
        AimMacroDeadZoneShape shape = AimMacroDeadZoneShape.Radial) =>
        new("Test", new[] { AimMacroButton.FnL, AimMacroButton.FnR }, deadZone, shape, null);

    private static AimMacroRecoil Recoil(double pullY, double driftX = 0.0, int delay = 0,
        int ramp = 0, byte threshold = 30, int[] times = null, double[] pulls = null) =>
        new(threshold, delay, ramp, pullY, driftX, times, pulls);

    private static AimMacroRotate Rotate(double radius, int period = 60, bool clockwise = true,
        AimMacroWhenFiring whenFiring = AimMacroWhenFiring.Any, double fadeAbove = 0.0,
        double? radiusY = null) =>
        new(radius, radiusY ?? radius, period, clockwise, whenFiring, fadeAbove);

    private static DS4MappedStickAxis Axis(double unit)
    {
        Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(
            AimMacroMath.ToCoordinate(unit), out var axis));
        return axis;
    }

    private static bool Run(ref AimMacroState state, AimMacroSettings settings, object layer,
        AimMacroRecoil recoil, AimMacroRotate rotate, double ux, double uy, byte r2, long now,
        AimMacroOutputPrecision precision, out double outX, out double outY)
    {
        bool touched = AimMacroMath.Apply(ref state, true, settings, layer, recoil, rotate,
            Axis(ux), Axis(uy), r2, now, precision, out var x, out var y);
        outX = AimMacroMath.ToUnit(x);
        outY = AimMacroMath.ToUnit(y);
        return touched;
    }

    private static void Game(double vx, double vy, AimMacroSettings settings,
        out double gx, out double gy) =>
        AimMacroMath.ToGame(vx, vy, settings.GameDeadZone,
            settings.GameDeadZoneShape == AimMacroDeadZoneShape.Radial, out gx, out gy);

    // ---- Untouched -------------------------------------------------------

    [TestMethod]
    public void NoContribution_ReturnsUntouched_InputsNotReEncoded()
    {
        var settings = Settings(0.1);
        var recoil = Recoil(0.1);
        var rotate = Rotate(0.1);
        // A high-resolution value that a byte or coordinate round trip would change.
        var x = DS4MappedStickAxis.FromSigned(1234);
        var y = DS4MappedStickAxis.FromSigned(-777);
        var state = new AimMacroState();

        void AssertUntouched(bool armed, AimMacroSettings s, object layer,
            AimMacroRecoil rc, AimMacroRotate rt, byte r2, long now, string why)
        {
            bool touched = AimMacroMath.Apply(ref state, armed, s, layer, rc, rt, x, y, r2, now,
                AimMacroOutputPrecision.HighRes, out var nx, out var ny);
            Assert.IsFalse(touched, why);
            Assert.IsTrue(nx.Equals(x) && ny.Equals(y), why);
        }

        AssertUntouched(false, settings, LayerA, recoil, rotate, 255, T0, "disarmed");
        AssertUntouched(true, null, LayerA, recoil, rotate, 255, T0, "no <AimMacros>");
        AssertUntouched(true, settings, null, recoil, rotate, 255, T0, "no layer");
        AssertUntouched(true, settings, LayerA, null, null, 255, T0, "layer has no macros");
        AssertUntouched(true, settings, LayerA, recoil, null, 0, T0, "recoil, not firing");
        AssertUntouched(true, settings, LayerA, Recoil(0.0), null, 255, T0, "zero pull");
        AssertUntouched(true, settings, LayerA, null, Rotate(0.0), 0, T0, "zero radius");
        AssertUntouched(true, settings, LayerA, null,
            Rotate(0.1, whenFiring: AimMacroWhenFiring.FiringOnly), 0, T0, "circle needs firing");
        AssertUntouched(true, settings, LayerA, null,
            Rotate(0.1, whenFiring: AimMacroWhenFiring.NotFiring), 255, T0, "circle needs not firing");
        // Firing but inside Delay, then the first ramp report (ramp 0).
        var delayed = Recoil(0.1, delay: 20, ramp: 10);
        state.Reset();
        AssertUntouched(true, settings, LayerA, delayed, null, 255, T0, "delay start");
        AssertUntouched(true, settings, LayerA, delayed, null, 255, T0 + 19, "in delay");
        AssertUntouched(true, settings, LayerA, delayed, null, 255, T0 + 20, "ramp 0");
        Assert.IsTrue(AimMacroMath.Apply(ref state, true, settings, LayerA, delayed, null, x, y,
            255, T0 + 21, AimMacroOutputPrecision.HighRes, out _, out _), "ramp > 0 contributes");

        // The layer overload: null layer = untouched.
        Assert.IsFalse(AimMacroMath.Apply(ref state, true, settings, (AimLayerStickSettings)null,
            x, y, 255, T0, AimMacroOutputPrecision.Byte, out var lx, out var ly));
        Assert.IsTrue(lx.Equals(x) && ly.Equals(y));
        Assert.IsNull(state.Layer);
    }

    [TestMethod]
    public void Disarm_ResetsState()
    {
        var state = new AimMacroState();
        var settings = Settings(0.1);
        Assert.IsTrue(Run(ref state, settings, LayerA, Recoil(0.1), Rotate(0.1), 0, 0, 255, T0,
            AimMacroOutputPrecision.Byte, out _, out _));
        Assert.IsTrue(state.Firing && state.Rotating);
        AimMacroMath.Apply(ref state, false, settings, LayerA, Recoil(0.1), Rotate(0.1),
            Axis(0), Axis(0), 255, T0 + 1, AimMacroOutputPrecision.Byte, out _, out _);
        Assert.AreEqual(default(AimMacroState), state);
    }

    // ---- Game-space conversion --------------------------------------------

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(0.1)]
    [DataRow(0.25)]
    [DataRow(0.5)]
    [DataRow(0.9)]
    public void UserInput_RoundTripsThroughGameSpace(double d)
    {
        int checkedPoints = 0;
        for (int sx = -32768; sx <= 32767; sx += 1021)
        {
            for (int sy = -32768; sy <= 32767; sy += 997)
            {
                var ax = DS4MappedStickAxis.FromSigned((short)sx);
                var ay = DS4MappedStickAxis.FromSigned((short)sy);
                double vx = AimMacroMath.ToUnit(ax), vy = AimMacroMath.ToUnit(ay);

                // Radial: outside the dead zone circle.
                if (Math.Sqrt(vx * vx + vy * vy) > d)
                {
                    AimMacroMath.ToGame(vx, vy, d, true, out double gx, out double gy);
                    AimMacroMath.FromGame(gx, gy, d, true, out double rx, out double ry);
                    AssertSameCoordinate(ax, rx);
                    AssertSameCoordinate(ay, ry);
                    checkedPoints++;
                }
                // Axial: each axis outside its dead band (or exactly centre).
                if ((Math.Abs(vx) > d || vx == 0) && (Math.Abs(vy) > d || vy == 0))
                {
                    AimMacroMath.ToGame(vx, vy, d, false, out double gx, out double gy);
                    AimMacroMath.FromGame(gx, gy, d, false, out double rx, out double ry);
                    AssertSameCoordinate(ax, rx);
                    AssertSameCoordinate(ay, ry);
                    checkedPoints++;
                }
            }
        }
        Assert.IsTrue(checkedPoints > 1000, checkedPoints.ToString());

        static void AssertSameCoordinate(DS4MappedStickAxis original, double unit)
        {
            double back = AimMacroMath.ToUnit(Axis(unit));
            Assert.AreEqual(AimMacroMath.ToUnit(original), back, 1.0 / 32768.0);
        }
    }

    [TestMethod]
    public void ByteAxes_MapToUnitRange_PlusYIsDown()
    {
        Assert.AreEqual(-1.0, AimMacroMath.ToUnit(DS4MappedStickAxis.FromLegacy(0)));
        Assert.AreEqual(0.0, AimMacroMath.ToUnit(DS4MappedStickAxis.FromLegacy(128)));
        Assert.AreEqual(1.0, AimMacroMath.ToUnit(DS4MappedStickAxis.FromLegacy(255)));
        for (int b = 0; b <= 255; b++)
            Assert.AreEqual((double)b, AimMacroMath.ToCoordinate(
                AimMacroMath.ToUnit(DS4MappedStickAxis.FromLegacy((byte)b))), 1e-12);

        // A positive PullY moves the stick down: RY coordinate above 128.
        var state = new AimMacroState();
        Assert.IsTrue(AimMacroMath.Apply(ref state, true, Settings(0.0), LayerA, Recoil(0.2), null,
            Axis(0), Axis(0), 255, T0, AimMacroOutputPrecision.HighRes, out var x, out var y));
        Assert.AreEqual(128.0, x.ProfileCoordinate, Tight);
        Assert.AreEqual(128.0 + 0.2 * 127.0, y.ProfileCoordinate, 1e-6);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ZeroGameDeadZone_IsPlainAddition(bool axial)
    {
        var settings = Settings(0.0, axial ? AimMacroDeadZoneShape.Axial : AimMacroDeadZoneShape.Radial);
        var recoil = Recoil(0.1, driftX: 0.05);
        double[][] users = { new[] { 0.0, 0.0 }, new[] { 0.3, -0.2 }, new[] { -0.6, 0.4 },
            new[] { 0.01, 0.0 } };
        foreach (double[] user in users)
        {
            var state = new AimMacroState();
            Assert.IsTrue(Run(ref state, settings, LayerA, recoil, null, user[0], user[1], 255, T0,
                AimMacroOutputPrecision.HighRes, out double ox, out double oy));
            Assert.AreEqual(user[0] + 0.05, ox, 1e-6);
            Assert.AreEqual(user[1] + 0.1, oy, 1e-6);
        }
    }

    [TestMethod]
    public void RadialDeadZone_SmallPullLandsPastTheDeadZone()
    {
        // Stick centred, 2% pull, game dead zone 10%: output = 0.1 + 0.02 * 0.9.
        var state = new AimMacroState();
        Assert.IsTrue(Run(ref state, Settings(0.1), LayerA, Recoil(0.02), null, 0, 0, 255, T0,
            AimMacroOutputPrecision.HighRes, out double ox, out double oy));
        Assert.AreEqual(0.0, ox, Tight);
        Assert.AreEqual(0.1 + 0.02 * 0.9, oy, 1e-6);

        // User aiming right at 50%: the pull adds in game space, so the game
        // sees exactly (g_user, 0.02).
        state.Reset();
        Assert.IsTrue(Run(ref state, Settings(0.1), LayerA, Recoil(0.02), null, 0.5, 0, 255, T0,
            AimMacroOutputPrecision.HighRes, out ox, out oy));
        AimMacroMath.ToGame(ox, oy, 0.1, true, out double gx, out double gy);
        Assert.AreEqual((0.5 - 0.1) / 0.9, gx, 1e-5);
        Assert.AreEqual(0.02, gy, 1e-5);
    }

    [TestMethod]
    public void AxialDeadZone_PerAxis()
    {
        // Axial: each axis gets its own dead band, so a pure Y pull with the
        // user aiming right leaves X's output unchanged.
        var settings = Settings(0.1, AimMacroDeadZoneShape.Axial);
        var state = new AimMacroState();
        Assert.IsTrue(Run(ref state, settings, LayerA, Recoil(0.02), null, 0.5, 0, 255, T0,
            AimMacroOutputPrecision.HighRes, out double ox, out double oy));
        Assert.AreEqual(0.5, ox, 1e-6);
        Assert.AreEqual(0.1 + 0.02 * 0.9, oy, 1e-6);
        // A user Y inside the dead band counts as 0 in game space.
        state.Reset();
        Assert.IsTrue(Run(ref state, settings, LayerA, Recoil(0.02), null, 0.5, 0.05, 255, T0,
            AimMacroOutputPrecision.HighRes, out ox, out oy));
        Assert.AreEqual(0.1 + 0.02 * 0.9, oy, 1e-6);
        // Negative drift past the band on X.
        state.Reset();
        Assert.IsTrue(Run(ref state, settings, LayerA, Recoil(0.0, driftX: -0.05), null, 0, 0, 255,
            T0, AimMacroOutputPrecision.HighRes, out ox, out oy));
        Assert.AreEqual(-(0.1 + 0.05 * 0.9), ox, 1e-6);
        Assert.AreEqual(0.0, oy, Tight);
    }

    // ---- Recoil timeline --------------------------------------------------

    [TestMethod]
    public void Recoil_DelayThenRampThenHold()
    {
        var settings = Settings(0.0);
        var recoil = Recoil(0.2, driftX: -0.1, delay: 50, ramp: 100);
        var state = new AimMacroState();
        for (int t = 0; t <= 400; t++)
        {
            bool touched = Run(ref state, settings, LayerA, recoil, null, 0, 0, 255, T0 + t,
                AimMacroOutputPrecision.HighRes, out double ox, out double oy);
            double expected = t <= 50 ? 0.0 : Math.Min(1.0, (t - 50) / 100.0);
            Assert.AreEqual(expected > 0, touched, $"t={t}");
            if (touched)
            {
                Assert.AreEqual(0.2 * expected, oy, 1e-6, $"t={t}");
                Assert.AreEqual(-0.1 * expected, ox, 1e-6, $"t={t}");
            }
        }
        Assert.AreEqual(0.0, AimMacroMath.RecoilScale(recoil, 49, out _));
        Assert.AreEqual(0.0, AimMacroMath.RecoilScale(recoil, 50, out _));
        Assert.AreEqual(0.5, AimMacroMath.RecoilScale(recoil, 100, out _), Tight);
        Assert.AreEqual(1.0, AimMacroMath.RecoilScale(recoil, 150, out double pull));
        Assert.AreEqual(0.2, pull);
    }

    [TestMethod]
    public void Recoil_ThresholdIsStrictlyAbove()
    {
        var recoil = Recoil(0.1, threshold: 100);
        var state = new AimMacroState();
        Assert.IsFalse(Run(ref state, Settings(0.0), LayerA, recoil, null, 0, 0, 100, T0,
            AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.IsFalse(state.Firing);
        Assert.IsTrue(Run(ref state, Settings(0.0), LayerA, recoil, null, 0, 0, 101, T0 + 1,
            AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.AreEqual(T0 + 1, state.FireStartMs);
    }

    [TestMethod]
    public void Recoil_PatternInterpolatesAndHoldsLast()
    {
        // <Pattern>0:8,300:5,1200:4</Pattern> starting at 100 ms here, so the
        // first value is held before it too.
        var recoil = Recoil(0.5, times: new[] { 100, 300, 1200 },
            pulls: new[] { 0.08, 0.05, 0.04 });
        Assert.AreEqual(0.08, AimMacroMath.PatternPull(recoil, 0), Tight);
        Assert.AreEqual(0.08, AimMacroMath.PatternPull(recoil, 100), Tight);
        Assert.AreEqual(0.065, AimMacroMath.PatternPull(recoil, 200), Tight);
        Assert.AreEqual(0.05, AimMacroMath.PatternPull(recoil, 300), Tight);
        Assert.AreEqual(0.045, AimMacroMath.PatternPull(recoil, 750), Tight);
        Assert.AreEqual(0.04, AimMacroMath.PatternPull(recoil, 1200), Tight);
        Assert.AreEqual(0.04, AimMacroMath.PatternPull(recoil, 100_000), Tight);

        // Through Apply, PullY (0.5) is overridden; pattern time counts from
        // firing start and the ramp still scales it.
        var ramped = Recoil(0.5, ramp: 400, times: new[] { 100, 300, 1200 },
            pulls: new[] { 0.08, 0.05, 0.04 });
        var state = new AimMacroState();
        double atT200 = 0, atT750 = 0, atT5000 = 0;
        for (int t = 0; t <= 5000; t++)
        {
            Run(ref state, Settings(0.0), LayerA, ramped, null, 0, 0, 255, T0 + t,
                AimMacroOutputPrecision.HighRes, out _, out double oy);
            if (t == 200) atT200 = oy;
            if (t == 750) atT750 = oy;
            if (t == 5000) atT5000 = oy;
        }
        Assert.AreEqual(0.065 * 0.5, atT200, 1e-6);
        Assert.AreEqual(0.045, atT750, 1e-6);
        Assert.AreEqual(0.04, atT5000, 1e-6);

        // A single point is a constant.
        var single = Recoil(0.5, times: new[] { 0 }, pulls: new[] { 0.07 });
        Assert.AreEqual(0.07, AimMacroMath.PatternPull(single, 0), Tight);
        Assert.AreEqual(0.07, AimMacroMath.PatternPull(single, 999), Tight);
    }

    [TestMethod]
    public void Recoil_ResetsOnReleaseAndOnLayerChange()
    {
        var settings = Settings(0.0);
        var recoil = Recoil(0.2, ramp: 100);
        var state = new AimMacroState();
        long t = T0;
        for (int i = 0; i < 200; i++, t++)
            Run(ref state, settings, LayerA, recoil, null, 0, 0, 255, t,
                AimMacroOutputPrecision.HighRes, out _, out _);
        Assert.IsTrue(Run(ref state, settings, LayerA, recoil, null, 0, 0, 255, t++,
            AimMacroOutputPrecision.HighRes, out _, out double full));
        Assert.AreEqual(0.2, full, 1e-6);

        // Release for one report: untouched, timer cleared.
        Assert.IsFalse(Run(ref state, settings, LayerA, recoil, null, 0, 0, 0, t++,
            AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.IsFalse(state.Firing);
        // Fire again: ramp starts from 0.
        Assert.IsFalse(Run(ref state, settings, LayerA, recoil, null, 0, 0, 255, t,
            AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.IsTrue(Run(ref state, settings, LayerA, recoil, null, 0, 0, 255, t + 50,
            AimMacroOutputPrecision.HighRes, out _, out double half));
        Assert.AreEqual(0.1, half, 1e-6);
        t += 200;
        Assert.IsTrue(Run(ref state, settings, LayerA, recoil, null, 0, 0, 255, t++,
            AimMacroOutputPrecision.HighRes, out _, out full));
        Assert.AreEqual(0.2, full, 1e-6);

        // Layer change with R2 still held: restarts from this report.
        Assert.IsFalse(Run(ref state, settings, LayerB, recoil, null, 0, 0, 255, t,
            AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.AreSame(LayerB, state.Layer);
        Assert.AreEqual(t, state.FireStartMs);
        Assert.IsTrue(Run(ref state, settings, LayerB, recoil, null, 0, 0, 255, t + 25,
            AimMacroOutputPrecision.HighRes, out _, out double quarter));
        Assert.AreEqual(0.05, quarter, 1e-6);

        // No layer for a report, then back: restarts again.
        Assert.IsFalse(AimMacroMath.Apply(ref state, true, settings, null, recoil, null,
            Axis(0), Axis(0), 255, t + 26, AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.IsFalse(Run(ref state, settings, LayerB, recoil, null, 0, 0, 255, t + 27,
            AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.AreEqual(t + 27, state.FireStartMs);
    }

    // ---- Circle -------------------------------------------------------

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public void Circle_OnRadiusAndTurnsTheRightWay(bool axial, bool clockwise)
    {
        const int Period = 60;
        const double Radius = 0.1;
        var settings = Settings(0.1, axial ? AimMacroDeadZoneShape.Axial : AimMacroDeadZoneShape.Radial);
        var rotate = Rotate(Radius, Period, clockwise);
        var state = new AimMacroState();
        double previous = double.NaN;
        for (int t = 0; t <= Period; t++)
        {
            bool touched = Run(ref state, settings, LayerA, null, rotate, 0, 0, 0, T0 + t,
                AimMacroOutputPrecision.HighRes, out double ox, out double oy);
            Assert.IsTrue(touched, $"t={t}");
            Game(ox, oy, settings, out double gx, out double gy);
            double length = Math.Sqrt(gx * gx + gy * gy);
            Assert.AreEqual(Radius, length, Radius * 0.02, $"t={t}");
            // +Y = down, so atan2(gy, gx) grows clockwise on screen.
            double angle = Math.Atan2(gy, gx);
            if (t == 0)
                Assert.AreEqual(0.0, angle, 0.02, "phase starts at 0 (right)");
            else
            {
                double step = Math.IEEERemainder(angle - previous, 2 * Math.PI);
                double expected = 2 * Math.PI / Period * (clockwise ? 1 : -1);
                Assert.AreEqual(expected, step, Math.Abs(expected) * 0.2, $"t={t}");
            }
            previous = angle;
        }
        // At a quarter period: CW is down (+Y), CCW is up.
        state.Reset();
        Run(ref state, settings, LayerA, null, rotate, 0, 0, 0, T0,
            AimMacroOutputPrecision.HighRes, out _, out _);
        Run(ref state, settings, LayerA, null, rotate, 0, 0, 0, T0 + Period / 4,
            AimMacroOutputPrecision.HighRes, out double qx, out double qy);
        Game(qx, qy, settings, out double qgx, out double qgy);
        Assert.AreEqual(0.0, qgx, 1e-6);
        Assert.AreEqual(clockwise ? Radius : -Radius, qgy, 1e-6);
        Assert.AreEqual(clockwise ? 0.1 + Radius * 0.9 : -(0.1 + Radius * 0.9), qy, 1e-6);
    }

    [TestMethod]
    public void Circle_EllipseUsesRadiusY()
    {
        var state = new AimMacroState();
        var rotate = Rotate(0.1, 40, radiusY: 0.05);
        Run(ref state, Settings(0.0), LayerA, null, rotate, 0, 0, 0, T0,
            AimMacroOutputPrecision.HighRes, out double x0, out double y0);
        Run(ref state, Settings(0.0), LayerA, null, rotate, 0, 0, 0, T0 + 10,
            AimMacroOutputPrecision.HighRes, out double x1, out double y1);
        Assert.AreEqual(0.1, x0, 1e-6);
        Assert.AreEqual(0.0, y0, 1e-6);
        Assert.AreEqual(0.0, x1, 1e-6);
        Assert.AreEqual(0.05, y1, 1e-6);
    }

    [TestMethod]
    public void Circle_PhaseResetsWhenItStopsAndOnLayerChange()
    {
        var settings = Settings(0.0);
        var rotate = Rotate(0.1, 60, whenFiring: AimMacroWhenFiring.FiringOnly);
        var state = new AimMacroState();
        Run(ref state, settings, LayerA, null, rotate, 0, 0, 255, T0,
            AimMacroOutputPrecision.HighRes, out _, out _);
        Run(ref state, settings, LayerA, null, rotate, 0, 0, 255, T0 + 15,
            AimMacroOutputPrecision.HighRes, out double x, out double y);
        Assert.AreEqual(0.0, x, 1e-6);
        Assert.AreEqual(0.1, y, 1e-6);
        // Stops (not firing), restarts at phase 0.
        Assert.IsFalse(Run(ref state, settings, LayerA, null, rotate, 0, 0, 0, T0 + 16,
            AimMacroOutputPrecision.HighRes, out _, out _));
        Assert.IsFalse(state.Rotating);
        Run(ref state, settings, LayerA, null, rotate, 0, 0, 255, T0 + 37,
            AimMacroOutputPrecision.HighRes, out x, out y);
        Assert.AreEqual(0.1, x, 1e-6);
        Assert.AreEqual(0.0, y, 1e-6);
        Run(ref state, settings, LayerA, null, rotate, 0, 0, 255, T0 + 52,
            AimMacroOutputPrecision.HighRes, out _, out y);
        Assert.AreEqual(0.1, y, 1e-6);
        // Layer change: phase 0 again on this report.
        Run(ref state, settings, LayerB, null, rotate, 0, 0, 255, T0 + 53,
            AimMacroOutputPrecision.HighRes, out x, out y);
        Assert.AreEqual(0.1, x, 1e-6);
        Assert.AreEqual(0.0, y, 1e-6);
    }

    [TestMethod]
    public void Circle_WhenFiringUsesRecoilThresholdElseR2Above30()
    {
        var rotate = Rotate(0.1, whenFiring: AimMacroWhenFiring.FiringOnly);
        Assert.IsFalse(AimMacroMath.RotateWanted(rotate, null, 30));
        Assert.IsTrue(AimMacroMath.RotateWanted(rotate, null, 31));
        var recoil = Recoil(0.1, threshold: 100);
        Assert.IsFalse(AimMacroMath.RotateWanted(rotate, recoil, 100));
        Assert.IsTrue(AimMacroMath.RotateWanted(rotate, recoil, 101));
        var notFiring = Rotate(0.1, whenFiring: AimMacroWhenFiring.NotFiring);
        Assert.IsTrue(AimMacroMath.RotateWanted(notFiring, recoil, 100));
        Assert.IsFalse(AimMacroMath.RotateWanted(notFiring, recoil, 101));
        Assert.IsTrue(AimMacroMath.RotateWanted(Rotate(0.1), null, 0));
        Assert.IsTrue(AimMacroMath.RotateWanted(Rotate(0.1), null, 255));
    }

    [TestMethod]
    public void FadeAbove_ShrinksLinearlyToZeroAtFullStick()
    {
        Assert.AreEqual(1.0, AimMacroMath.FadeScale(0.0, 1.0));
        Assert.AreEqual(1.0, AimMacroMath.FadeScale(0.4, 0.2));
        Assert.AreEqual(1.0, AimMacroMath.FadeScale(0.4, 0.4));
        Assert.AreEqual(0.75, AimMacroMath.FadeScale(0.4, 0.55), Tight);
        Assert.AreEqual(0.5, AimMacroMath.FadeScale(0.4, 0.7), Tight);
        Assert.AreEqual(0.25, AimMacroMath.FadeScale(0.4, 0.85), Tight);
        Assert.AreEqual(0.0, AimMacroMath.FadeScale(0.4, 1.0), Tight);
        Assert.AreEqual(1.0, AimMacroMath.FadeScale(1.0, 1.0));

        // Through Apply (game dead zone 0): user at 70% up, phase 0 point is
        // (radius * fade, 0) on top of the user's stick.
        var state = new AimMacroState();
        Assert.IsTrue(Run(ref state, Settings(0.0), LayerA, null, Rotate(0.1, fadeAbove: 0.4),
            0, -0.7, 0, T0, AimMacroOutputPrecision.HighRes, out double ox, out double oy));
        Assert.AreEqual(0.05, ox, 1e-6);
        Assert.AreEqual(-0.7, oy, 1e-6);
        // Full stick: the circle is gone, so nothing contributes.
        state.Reset();
        Assert.IsFalse(Run(ref state, Settings(0.0), LayerA, null, Rotate(0.1, fadeAbove: 0.4),
            -1.0, 0, 0, T0, AimMacroOutputPrecision.HighRes, out _, out _));
    }

    // ---- Clamp --------------------------------------------------------

    [TestMethod]
    public void Clamp_AtFullDeflection_KeepsDirection_Radial()
    {
        var settings = Settings(0.1);
        var state = new AimMacroState();
        Assert.IsTrue(Run(ref state, settings, LayerA, Recoil(0.3), null, 1.0, 0, 255, T0,
            AimMacroOutputPrecision.HighRes, out double ox, out double oy));
        Assert.AreEqual(1.0, Math.Sqrt(ox * ox + oy * oy), 1e-5);
        Assert.AreEqual(Math.Atan2(0.3, 1.0), Math.Atan2(oy, ox), 1e-5);

        // Diagonal full stick pulled further down-left.
        state.Reset();
        double u = Math.Sqrt(0.5);
        Assert.IsTrue(Run(ref state, settings, LayerA, Recoil(0.3, driftX: -0.2), null, -u, u,
            255, T0, AimMacroOutputPrecision.HighRes, out ox, out oy));
        Assert.AreEqual(1.0, Math.Sqrt(ox * ox + oy * oy), 1e-5);
        Assert.AreEqual(Math.Atan2(u + 0.3, -u - 0.2), Math.Atan2(oy, ox), 1e-5);
    }

    [TestMethod]
    public void Clamp_AtFullDeflection_KeepsDirection_Axial()
    {
        var settings = Settings(0.1, AimMacroDeadZoneShape.Axial);
        var state = new AimMacroState();
        // User right 100%, down 50%; drift right 20% overflows X.
        Assert.IsTrue(Run(ref state, settings, LayerA, Recoil(0.0, driftX: 0.2), null, 1.0, 0.5,
            255, T0, AimMacroOutputPrecision.HighRes, out double ox, out double oy));
        Game(ox, oy, settings, out double gx, out double gy);
        double userGy = (0.5 - 0.1) / 0.9;
        Assert.AreEqual(1.0, gx, 1e-5);
        Assert.AreEqual(userGy / 1.2, gy, 1e-5);
        Assert.AreEqual(1.0, ox, 1e-5);
    }

    [TestMethod]
    public void Clamp_CornerBeyondCircle_IsNotPulledIn()
    {
        // A square-stick corner (1, 1) is longer than 1; a tiny pull must not
        // drag it onto the unit circle.
        var state = new AimMacroState();
        Assert.IsTrue(Run(ref state, Settings(0.1), LayerA, Recoil(0.01), null, 1.0, -1.0, 255,
            T0, AimMacroOutputPrecision.HighRes, out double ox, out double oy));
        Assert.IsTrue(Math.Abs(ox) > 0.99 && Math.Abs(oy) > 0.97, $"{ox}, {oy}");
    }

    // ---- Dither -------------------------------------------------------

    [TestMethod]
    [DataRow(0.0123, 0.0, 0.0, 0.0)]
    [DataRow(0.031, -0.017, 0.1, 0.0)]
    [DataRow(0.005, 0.0, 0.1, 0.33)]
    public void Dither_AveragesToExact_WithinOneByte(double pull, double drift, double d,
        double userX)
    {
        var settings = Settings(d);
        var recoil = Recoil(pull, driftX: drift);
        // Exact coordinates the high-res path gives.
        var exactState = new AimMacroState();
        Assert.IsTrue(AimMacroMath.Apply(ref exactState, true, settings, LayerA, recoil, null,
            Axis(userX), Axis(0), 255, T0, AimMacroOutputPrecision.HighRes,
            out var exactX, out var exactY));

        var state = new AimMacroState();
        double sumX = 0, sumY = 0;
        const int Reports = 1000;
        for (int i = 0; i < Reports; i++)
        {
            Assert.IsTrue(AimMacroMath.Apply(ref state, true, settings, LayerA, recoil, null,
                Axis(userX), Axis(0), 255, T0 + i, AimMacroOutputPrecision.Byte,
                out var x, out var y));
            Assert.IsFalse(x.IsHighResolution || y.IsHighResolution);
            Assert.IsTrue(Math.Abs(x.LegacyValue - exactX.ProfileCoordinate) <= 1.0, $"x {i}");
            Assert.IsTrue(Math.Abs(y.LegacyValue - exactY.ProfileCoordinate) <= 1.0, $"y {i}");
            sumX += x.LegacyValue;
            sumY += y.LegacyValue;
        }
        Assert.AreEqual(exactX.ProfileCoordinate, sumX / Reports, 0.01);
        Assert.AreEqual(exactY.ProfileCoordinate, sumY / Reports, 0.01);

        // Stop firing: untouched and the carry is gone.
        Assert.IsFalse(AimMacroMath.Apply(ref state, true, settings, LayerA, recoil, null,
            Axis(userX), Axis(0), 0, T0 + Reports, AimMacroOutputPrecision.Byte, out _, out _));
        Assert.AreEqual(0.0, state.DitherErrorX);
        Assert.AreEqual(0.0, state.DitherErrorY);
    }

    [TestMethod]
    public void Dither_CarryClearedOnDisarm()
    {
        var state = new AimMacroState();
        var recoil = Recoil(0.0123);
        AimMacroMath.Apply(ref state, true, Settings(0.0), LayerA, recoil, null, Axis(0), Axis(0),
            255, T0, AimMacroOutputPrecision.Byte, out _, out _);
        Assert.AreNotEqual(0.0, state.DitherErrorY);
        AimMacroMath.Apply(ref state, false, Settings(0.0), LayerA, recoil, null, Axis(0), Axis(0),
            255, T0 + 1, AimMacroOutputPrecision.Byte, out _, out _);
        Assert.AreEqual(0.0, state.DitherErrorY);
    }

    // ---- Hot path -----------------------------------------------------

    [TestMethod]
    public void Apply_DoesNotAllocate()
    {
        var radial = Settings(0.1);
        var axial = Settings(0.1, AimMacroDeadZoneShape.Axial);
        var recoil = Recoil(0.06, driftX: 0.01, delay: 10, ramp: 60,
            times: new[] { 0, 300, 1200 }, pulls: new[] { 0.08, 0.05, 0.04 });
        var rotate = Rotate(0.1, 60, clockwise: false, fadeAbove: 0.4);
        var state = new AimMacroState();
        DS4MappedStickAxis sink = default;
        int touched = 0;

        for (int i = 0; i < 2000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.IsTrue(touched > 10000, touched.ToString());
        GC.KeepAlive(sink);

        void Step(int i)
        {
            // 500-report cycle: idle, aim (circle), aim + fire, layer swap,
            // disarmed; both precisions and both shapes.
            int phase = i % 500;
            object layer = phase < 300 ? LayerA : LayerB;
            byte r2 = (byte)(phase >= 100 && phase < 400 ? 255 : 0);
            bool armed = phase < 450;
            var settings = (i / 500) % 2 == 0 ? radial : axial;
            var precision = (i / 1000) % 2 == 0 ? AimMacroOutputPrecision.Byte :
                AimMacroOutputPrecision.HighRes;
            var x = DS4MappedStickAxis.FromLegacy((byte)(i * 7));
            var y = DS4MappedStickAxis.FromSigned((short)(i * 13));
            if (AimMacroMath.Apply(ref state, armed, settings, layer, recoil, rotate, x, y, r2,
                    T0 + i, precision, out var nx, out var ny))
            {
                touched++;
                sink = nx.ProfileCoordinate > ny.ProfileCoordinate ? nx : ny;
            }
        }
    }
}
