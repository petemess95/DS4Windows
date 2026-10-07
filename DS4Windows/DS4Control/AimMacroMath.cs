using System;

namespace DS4Windows
{
    // How the macro step writes the stick: Byte (ViiperDS4: the wire carries
    // bytes) dithers to a legacy byte with a carry; HighRes (Xbox/Switch
    // outputs) keeps the exact fractional coordinate.
    internal enum AimMacroOutputPrecision { Byte, HighRes }

    /// <summary>
    /// Per-device macro state carried between reports. Owned by the caller
    /// and passed by ref; only the input thread touches it. Default = idle.
    /// </summary>
    internal struct AimMacroState
    {
        // Active layer last report (reference identity); null = macros were
        // idle. A different reference restarts the recoil timer and phase.
        internal object Layer;
        // Recoil: R2 above FireThreshold with this layer since FireStartMs.
        internal bool Firing;
        internal long FireStartMs;
        // Circle: running since RotateStartMs (phase 0 there).
        internal bool Rotating;
        internal long RotateStartMs;
        // Byte output: rounding error carried to the next report, per axis,
        // in stick coordinates. Cleared whenever the macros stop contributing.
        internal double DitherErrorX;
        internal double DitherErrorY;

        internal void Reset() => this = default;
    }

    /// <summary>
    /// Recoil compensation and the rotational circle (Phase 7), worked out in
    /// "game space": the deflection the game sees after its own right-stick
    /// dead zone. Pure apart from the caller's state struct; lock- and
    /// allocation-free, so it can run on every report.
    ///
    /// Axis convention (DS4State/DS4MappedStickAxis): ProfileCoordinate is
    /// 0..255 with 128 = centre, X 255 = right, Y 255 = down (+Y = down, as
    /// in the Atan2(-(y - 128), x - 128) angle code). The unit deflection used
    /// here is offset/127 above centre and offset/128 below, the same scale
    /// as DS4MappedStickAxis.ToSigned16, so byte 0 = -1 and 255 = +1.
    /// </summary>
    internal static class AimMacroMath
    {
        // Rotate.WhenFiring's firing rule when the layer has no <Recoil>.
        internal const byte DefaultFireThreshold = AimMacroParser.DefaultFireThreshold;

        /// <summary>Convenience for the report loop: the active layer is its own identity.</summary>
        internal static bool Apply(ref AimMacroState state, bool armed,
            AimMacroSettings settings, AimLayerStickSettings layer,
            in DS4MappedStickAxis axisX, in DS4MappedStickAxis axisY, byte r2, long nowMs,
            AimMacroOutputPrecision precision,
            out DS4MappedStickAxis newX, out DS4MappedStickAxis newY) =>
            Apply(ref state, armed, settings, layer, layer?.Recoil, layer?.Rotate,
                axisX, axisY, r2, nowMs, precision, out newX, out newY);

        /// <summary>
        /// One report. axisX/axisY are the final mapped right stick; r2 is the
        /// raw trigger; nowMs is the clock AimLayerState.Select uses;
        /// layerIdentity is the active layer (null = none). Returns false when
        /// no macro contributes: newX/newY are then the inputs unchanged and
        /// the caller should leave its axes alone (no re-encode). Timers still
        /// advance on such reports (recoil delay, a zero circle point).
        /// </summary>
        internal static bool Apply(ref AimMacroState state, bool armed,
            AimMacroSettings settings, object layerIdentity,
            AimMacroRecoil recoil, AimMacroRotate rotate,
            in DS4MappedStickAxis axisX, in DS4MappedStickAxis axisY, byte r2, long nowMs,
            AimMacroOutputPrecision precision,
            out DS4MappedStickAxis newX, out DS4MappedStickAxis newY)
        {
            newX = axisX;
            newY = axisY;
            if (!armed || settings == null || layerIdentity == null ||
                (recoil == null && rotate == null))
            {
                state.Reset();
                return false;
            }

            if (!ReferenceEquals(layerIdentity, state.Layer))
            {
                // Layer change: both timers restart; a trigger already held
                // counts from this report (as AimLayerState.Select does).
                state.Reset();
                state.Layer = layerIdentity;
            }

            double offsetX = 0.0, offsetY = 0.0;

            // Recoil timer: starts on the first report R2 is above the
            // threshold with this layer; R2 dropping resets it.
            if (recoil != null && r2 > recoil.FireThreshold)
            {
                if (!state.Firing)
                {
                    state.Firing = true;
                    state.FireStartMs = nowMs;
                }
                double scale = RecoilScale(recoil, nowMs - state.FireStartMs, out double pullY);
                offsetX = recoil.DriftX * scale;
                offsetY = pullY * scale;
            }
            else
            {
                state.Firing = false;
            }

            double vx = ToUnit(axisX), vy = ToUnit(axisY);
            double d = settings.GameDeadZone;
            bool radial = settings.GameDeadZoneShape == AimMacroDeadZoneShape.Radial;
            ToGame(vx, vy, d, radial, out double gx, out double gy);

            if (rotate != null && (rotate.Radius > 0.0 || rotate.RadiusY > 0.0) &&
                RotateWanted(rotate, recoil, r2))
            {
                if (!state.Rotating)
                {
                    state.Rotating = true;
                    state.RotateStartMs = nowMs;
                }
                double phase = Phase(rotate, nowMs - state.RotateStartMs);
                double fade = FadeScale(rotate.FadeAbove,
                    Math.Min(1.0, Math.Sqrt(gx * gx + gy * gy)));
                offsetX += fade * rotate.Radius * Math.Cos(phase);
                offsetY += fade * rotate.RadiusY * Math.Sin(phase);
            }
            else
            {
                state.Rotating = false;
            }

            if (offsetX == 0.0 && offsetY == 0.0)
            {
                // Nothing to add: leave the stick exactly as mapped.
                state.DitherErrorX = 0.0;
                state.DitherErrorY = 0.0;
                return false;
            }

            double userLength = Math.Sqrt(gx * gx + gy * gy);
            Clamp(gx + offsetX, gy + offsetY, radial, userLength,
                out double totalX, out double totalY);
            FromGame(totalX, totalY, d, radial, out double outX, out double outY);
            double coordX = ToCoordinate(outX), coordY = ToCoordinate(outY);

            if (precision == AimMacroOutputPrecision.Byte)
            {
                newX = DS4MappedStickAxis.FromLegacy(
                    GyroMouseStickMath.DitherAxis(coordX, ref state.DitherErrorX));
                newY = DS4MappedStickAxis.FromLegacy(
                    GyroMouseStickMath.DitherAxis(coordY, ref state.DitherErrorY));
            }
            else
            {
                DS4MappedStickAxis.TryFromProfileCoordinate(coordX, out newX);
                DS4MappedStickAxis.TryFromProfileCoordinate(coordY, out newY);
            }
            return true;
        }

        // Rotate.WhenFiring: firing = the layer's Recoil threshold, else R2 > 30.
        internal static bool RotateWanted(AimMacroRotate rotate, AimMacroRecoil recoil, byte r2)
        {
            if (rotate.WhenFiring == AimMacroWhenFiring.Any)
                return true;
            bool firing = r2 > (recoil?.FireThreshold ?? DefaultFireThreshold);
            return rotate.WhenFiring == AimMacroWhenFiring.FiringOnly ? firing : !firing;
        }

        /// <summary>
        /// Recoil at firingMs since firing started: returns the ramp factor
        /// (0 during Delay, then 0..1 over Ramp; Ramp 0 = 1 at once) and the
        /// PullY to scale by it (Pattern, measured from firing start, linear
        /// between points, first/last value held outside them; else PullY).
        /// </summary>
        internal static double RecoilScale(AimMacroRecoil recoil, long firingMs, out double pullY)
        {
            if (firingMs < 0)
                firingMs = 0;
            pullY = recoil.HasPattern ? PatternPull(recoil, firingMs) : recoil.PullY;
            long afterDelay = firingMs - recoil.DelayMs;
            if (afterDelay < 0)
                return 0.0;
            if (recoil.RampMs <= 0 || afterDelay >= recoil.RampMs)
                return 1.0;
            return (double)afterDelay / recoil.RampMs;
        }

        internal static double PatternPull(AimMacroRecoil recoil, long firingMs)
        {
            ReadOnlySpan<int> times = recoil.PatternTimesMs;
            ReadOnlySpan<double> pulls = recoil.PatternPullY;
            if (firingMs <= times[0])
                return pulls[0];
            for (int index = 1; index < times.Length; index++)
            {
                if (firingMs <= times[index])
                {
                    double t = (double)(firingMs - times[index - 1]) /
                        (times[index] - times[index - 1]);
                    return pulls[index - 1] + (pulls[index] - pulls[index - 1]) * t;
                }
            }
            return pulls[pulls.Length - 1];
        }

        // Phase in radians at elapsedMs since the circle started; +Y is down,
        // so a growing angle (right, then down) turns clockwise on screen.
        internal static double Phase(AimMacroRotate rotate, long elapsedMs)
        {
            if (elapsedMs < 0)
                elapsedMs = 0;
            // Modulo on the integer clock keeps the angle exact on long holds.
            double turn = (double)(elapsedMs % rotate.PeriodMs) / rotate.PeriodMs;
            double phase = 2.0 * Math.PI * turn;
            return rotate.Clockwise ? phase : -phase;
        }

        // 1 up to fadeAbove of the user's game-space deflection, then
        // linearly down to 0 at full deflection; fadeAbove 0 = never fade.
        internal static double FadeScale(double fadeAbove, double userDeflection)
        {
            if (fadeAbove <= 0.0 || userDeflection <= fadeAbove || fadeAbove >= 1.0)
                return 1.0;
            return Math.Max(0.0, (1.0 - userDeflection) / (1.0 - fadeAbove));
        }

        // Mapped stick coordinate -> -1..1 (asymmetric 128/127 scale).
        internal static double ToUnit(in DS4MappedStickAxis axis)
        {
            double offset = axis.ProfileCoordinate - 128.0;
            return offset < 0.0 ? offset / 128.0 : offset / 127.0;
        }

        internal static double ToCoordinate(double unit)
        {
            double coordinate = 128.0 + (unit < 0.0 ? unit * 128.0 : unit * 127.0);
            return double.IsNaN(coordinate) ? 128.0 : Math.Clamp(coordinate, 0.0, 255.0);
        }

        // Output deflection v -> what the game sees: g = max(0, |v| - d) / (1 - d),
        // on the vector length (Radial) or per axis (Axial).
        internal static void ToGame(double vx, double vy, double d, bool radial,
            out double gx, out double gy)
        {
            if (!radial)
            {
                gx = AxisToGame(vx, d);
                gy = AxisToGame(vy, d);
                return;
            }
            double length = Math.Sqrt(vx * vx + vy * vy);
            if (length <= d)
            {
                gx = gy = 0.0;
                return;
            }
            double scale = (length - d) / (1.0 - d) / length;
            gx = vx * scale;
            gy = vy * scale;
        }

        // Inverse of ToGame outside the dead zone: v' = d + |G|(1 - d) along
        // G (Radial) or per axis (Axial); G = 0 gives the exact centre.
        internal static void FromGame(double gx, double gy, double d, bool radial,
            out double vx, out double vy)
        {
            if (!radial)
            {
                vx = AxisFromGame(gx, d);
                vy = AxisFromGame(gy, d);
                return;
            }
            double length = Math.Sqrt(gx * gx + gy * gy);
            if (length == 0.0)
            {
                vx = vy = 0.0;
                return;
            }
            double scale = (d + length * (1.0 - d)) / length;
            vx = gx * scale;
            vy = gy * scale;
        }

        // Keep the game-space total within full deflection without turning
        // it: Radial caps the length at 1 (or the user's own length, so a
        // stick already past the circle in a corner is not pulled in);
        // Axial scales both axes by the larger overflow.
        internal static void Clamp(double gx, double gy, bool radial, double userLength,
            out double cx, out double cy)
        {
            double scale = 1.0;
            if (radial)
            {
                double length = Math.Sqrt(gx * gx + gy * gy);
                double limit = Math.Max(1.0, userLength);
                if (length > limit)
                    scale = limit / length;
            }
            else
            {
                double largest = Math.Max(Math.Abs(gx), Math.Abs(gy));
                if (largest > 1.0)
                    scale = 1.0 / largest;
            }
            cx = gx * scale;
            cy = gy * scale;
        }

        private static double AxisToGame(double v, double d)
        {
            double magnitude = Math.Abs(v) - d;
            if (magnitude <= 0.0)
                return 0.0;
            return Math.CopySign(magnitude / (1.0 - d), v);
        }

        private static double AxisFromGame(double g, double d) =>
            g == 0.0 ? 0.0 : Math.CopySign(d + Math.Abs(g) * (1.0 - d), g);
    }
}
