// Frozen copy of Mouse.SixMouseStickCore / SixMouseReset from before the
// Task 6.1 extraction (commit 8b8313e). Only the globals were turned into
// parameters and the post-map submit into out bytes; every int truncation,
// clamp and filter call is kept as it was. Do not "fix" this file: it pins
// GyroMouseStickMath with the defaults to today's exact output.
using System;
using DS4Windows;
using DS4Windows.Switch2;
using Sensorit.Base;

namespace DS4WindowsTests;

internal sealed class LegacyGyroMouseStickOracle
{
    // Same initial filter tuning as Mouse's constructor.
    internal readonly OneEuroFilterPair filterPair = new OneEuroFilterPair();

    internal LegacyGyroMouseStickOracle(double minCutoff, double beta)
    {
        filterPair.axis1Filter.MinCutoff = filterPair.axis2Filter.MinCutoff = minCutoff;
        filterPair.axis1Filter.Beta = filterPair.axis2Filter.Beta = beta;
    }

    internal const int SMOOTH_BUFFER_LEN = 3;
    internal int[] xSmoothBuffer = new int[SMOOTH_BUFFER_LEN];
    internal int[] ySmoothBuffer = new int[SMOOTH_BUFFER_LEN];
    internal int smoothBufferTail = 0;

    internal void SixMouseReset(SixAxisEventArgs args, GyroMouseStickInfo settings)
    {
        int iIndex = smoothBufferTail % SMOOTH_BUFFER_LEN;
        xSmoothBuffer[iIndex] = 0;
        ySmoothBuffer[iIndex] = 0;
        smoothBufferTail = iIndex + 1;

        GyroMouseStickInfo msinfo = settings;
        if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.OneEuro)
        {
            double currentRate = 1.0 / args.sixAxis.elapsed;
            filterPair.axis1Filter.Filter(0.0, currentRate);
            filterPair.axis2Filter.Filter(0.0, currentRate);
        }
    }

    internal void SixMouseStickCore(SixAxisEventArgs arg,
        in Switch2GyroTriggerModifierResult modifier,
        int horizontalAxis, GyroMouseStickInfo settings,
        out byte xOut, out byte yOut)
    {
        int deltaX = 0, deltaY = 0;
        deltaX = horizontalAxis == 0 ? arg.sixAxis.gyroYawFull :
            arg.sixAxis.gyroRollFull;
        deltaY = -arg.sixAxis.gyroPitchFull;
        //int inputX = deltaX, inputY = deltaY;
        int maxDirX = deltaX >= 0 ? 127 : -128;
        int maxDirY = deltaY >= 0 ? 127 : -128;

        GyroMouseStickInfo msinfo = settings;

        double tempDouble = arg.sixAxis.elapsed * 250.0; // Base default speed on 4 ms
        double tempAngle = Math.Atan2(-deltaY, deltaX);
        double normX = Math.Abs(Math.Cos(tempAngle));
        double normY = Math.Abs(Math.Sin(tempAngle));
        int signX = Math.Sign(deltaX);
        int signY = Math.Sign(deltaY);

        int deadzoneX = (int)Math.Abs(normX * msinfo.deadZone);
        int deadzoneY = (int)Math.Abs(normY * msinfo.deadZone);

        int maxValX = signX * msinfo.maxZone;
        int maxValY = signY * msinfo.maxZone;

        double xratio = 0.0, yratio = 0.0;
        double antiX = msinfo.antiDeadX * normX;
        double antiY = msinfo.antiDeadY * normY;

        if (Math.Abs(deltaX) > deadzoneX)
        {
            deltaX -= signX * deadzoneX;
            //deltaX = (int)(deltaX * tempDouble);
            deltaX = (deltaX < 0 && deltaX < maxValX) ? maxValX :
                (deltaX > 0 && deltaX > maxValX) ? maxValX : deltaX;
            //if (deltaX != maxValX) deltaX -= deltaX % (signX * GyroMouseFuzz);
        }
        else
        {
            deltaX = 0;
        }

        if (Math.Abs(deltaY) > deadzoneY)
        {
            deltaY -= signY * deadzoneY;
            //deltaY = (int)(deltaY * tempDouble);
            deltaY = (deltaY < 0 && deltaY < maxValY) ? maxValY :
                (deltaY > 0 && deltaY > maxValY) ? maxValY : deltaY;
            //if (deltaY != maxValY) deltaY -= deltaY % (signY * GyroMouseFuzz);
        }
        else
        {
            deltaY = 0;
        }

        if (modifier.DeadzoneActive)
        {
            deltaX = (int)Switch2GyroTriggerModifier.ApplySoftDeadzone(
                deltaX, normX, modifier.DeadzoneAmount);
            deltaY = (int)Switch2GyroTriggerModifier.ApplySoftDeadzone(
                deltaY, normY, modifier.DeadzoneAmount);
        }
        deltaX = (int)Switch2GyroTriggerModifier.ApplyDampening(
            deltaX, modifier);
        deltaY = (int)Switch2GyroTriggerModifier.ApplyDampening(
            deltaY, modifier);

        if (msinfo.jitterCompensation)
        {
            // Possibly expose threshold later
            const double threshold = 2;
            const float thresholdF = (float)threshold;

            double absX = Math.Abs(deltaX);
            if (absX <= normX * threshold)
            {
                deltaX = (int)(signX * Math.Pow(absX / thresholdF, 1.408) * threshold);
            }

            double absY = Math.Abs(deltaY);
            if (absY <= normY * threshold)
            {
                deltaY = (int)(signY * Math.Pow(absY / thresholdF, 1.408) * threshold);
            }
        }

        if (msinfo.useSmoothing)
        {
            if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.OneEuro)
            {
                double currentRate = 1.0 / arg.sixAxis.elapsed;
                deltaX = (int)(filterPair.axis1Filter.Filter(deltaX, currentRate));
                deltaY = (int)(filterPair.axis2Filter.Filter(deltaY, currentRate));
            }
            else if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.WeightedAverage)
            {
                int iIndex = smoothBufferTail % SMOOTH_BUFFER_LEN;
                xSmoothBuffer[iIndex] = deltaX;
                ySmoothBuffer[iIndex] = deltaY;
                smoothBufferTail = iIndex + 1;

                double currentWeight = 1.0;
                double finalWeight = 0.0;
                double x_out = 0.0, y_out = 0.0;
                int idx = 0;
                for (int i = 0; i < SMOOTH_BUFFER_LEN; i++)
                {
                    idx = (smoothBufferTail - i - 1 + SMOOTH_BUFFER_LEN) % SMOOTH_BUFFER_LEN;
                    x_out += xSmoothBuffer[idx] * currentWeight;
                    y_out += ySmoothBuffer[idx] * currentWeight;
                    finalWeight += currentWeight;
                    currentWeight *= msinfo.smoothWeight;
                }

                x_out /= finalWeight;
                deltaX = (int)x_out;
                y_out /= finalWeight;
                deltaY = (int)y_out;
            }

            maxValX = deltaX < 0 ? -msinfo.maxZone : msinfo.maxZone;
            maxValY = deltaY < 0 ? -msinfo.maxZone : msinfo.maxZone;
            maxDirX = deltaX >= 0 ? 127 : -128;
            maxDirY = deltaY >= 0 ? 127 : -128;
        }

        if (msinfo.vertScale != 100)
        {
            double verticalScale = msinfo.vertScale * 0.01;
            deltaY = (int)(deltaY * verticalScale);
            deltaY = (deltaY < 0 && deltaY < maxValY) ? maxValY :
                (deltaY > 0 && deltaY > maxValY) ? maxValY : deltaY;
        }

        if (deltaX != 0) xratio = deltaX / (double)maxValX;
        if (deltaY != 0) yratio = deltaY / (double)maxValY;

        if (msinfo.maxOutputEnabled)
        {
            double maxOutRatio = msinfo.maxOutput / 100.0;
            // Expand output a bit. Likely not going to get a straight line with Gyro
            double maxOutXRatio = Math.Min(normX / 0.95, 1.0) * maxOutRatio;
            double maxOutYRatio = Math.Min(normY / 0.95, 1.0) * maxOutRatio;

            xratio = Math.Min(Math.Max(xratio, 0.0), maxOutXRatio);
            yratio = Math.Min(Math.Max(yratio, 0.0), maxOutYRatio);
        }

        double xNorm = 0.0, yNorm = 0.0;
        if (xratio != 0.0)
        {
            xNorm = (1.0 - antiX) * xratio + antiX;
        }

        if (yratio != 0.0)
        {
            yNorm = (1.0 - antiY) * yratio + antiY;
        }

        if (msinfo.inverted != 0)
        {
            if ((msinfo.inverted & 1) == 1)
            {
                // Invert max dir value
                maxDirX = deltaX >= 0 ? -128 : 127;
            }

            if ((msinfo.inverted & 2) == 2)
            {
                // Invert max dir value
                maxDirY = deltaY >= 0 ? -128 : 127;
            }
        }

        byte axisXOut = (byte)(xNorm * maxDirX + 128.0);
        byte axisYOut = (byte)(yNorm * maxDirY + 128.0);

        xOut = axisXOut;
        yOut = axisYOut;
    }
}
