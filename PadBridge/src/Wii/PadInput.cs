using System;
using PadBridge.ViGEm;

namespace PadBridge.Wii
{
    /// <summary>Buttons named after the Nintendo layout: A = right, B = bottom, X = top, Y = left.</summary>
    [Flags]
    internal enum PadButton
    {
        None = 0,
        A = 1 << 0, B = 1 << 1, X = 1 << 2, Y = 1 << 3,
        L = 1 << 4, R = 1 << 5, ZL = 1 << 6, ZR = 1 << 7,
        Plus = 1 << 8, Minus = 1 << 9, Home = 1 << 10,
        LStick = 1 << 11, RStick = 1 << 12,
        Up = 1 << 13, Down = 1 << 14, Left = 1 << 15, Right = 1 << 16
    }

    internal struct PadInput
    {
        public PadButton Buttons;
        public float LX, LY, RX, RY; // -1..1, up is positive
        public float LT, RT;         // 0..1

        public void Set(PadButton b, bool on) { if (on) Buttons |= b; else Buttons &= ~b; }
        public bool Has(PadButton b) => (Buttons & b) != 0;
    }

    internal sealed class MappingOptions
    {
        /// <summary>true: buttons keep their position (Nintendo A on the right becomes Xbox B). false: labels match (A = A).</summary>
        public bool PositionalLayout = true;
        public float Deadzone = 0.10f;
    }

    internal static class Mapper
    {
        public static XReport ToXbox(PadInput p, MappingOptions o)
        {
            var r = new XReport();
            void Map(PadButton src, XButtons dst) { if (p.Has(src)) r.Buttons |= dst; }

            if (o.PositionalLayout)
            {
                Map(PadButton.A, XButtons.B); Map(PadButton.B, XButtons.A);
                Map(PadButton.X, XButtons.Y); Map(PadButton.Y, XButtons.X);
            }
            else
            {
                Map(PadButton.A, XButtons.A); Map(PadButton.B, XButtons.B);
                Map(PadButton.X, XButtons.X); Map(PadButton.Y, XButtons.Y);
            }
            Map(PadButton.L, XButtons.LeftShoulder); Map(PadButton.R, XButtons.RightShoulder);
            Map(PadButton.Plus, XButtons.Start); Map(PadButton.Minus, XButtons.Back);
            Map(PadButton.Home, XButtons.Guide);
            Map(PadButton.LStick, XButtons.LeftThumb); Map(PadButton.RStick, XButtons.RightThumb);
            Map(PadButton.Up, XButtons.DPadUp); Map(PadButton.Down, XButtons.DPadDown);
            Map(PadButton.Left, XButtons.DPadLeft); Map(PadButton.Right, XButtons.DPadRight);

            float lt = Math.Max(p.LT, p.Has(PadButton.ZL) ? 1f : 0f);
            float rt = Math.Max(p.RT, p.Has(PadButton.ZR) ? 1f : 0f);
            r.LeftTrigger = (byte)(Math.Max(0f, Math.Min(1f, lt)) * 255);
            r.RightTrigger = (byte)(Math.Max(0f, Math.Min(1f, rt)) * 255);

            Stick(p.LX, p.LY, o.Deadzone, out r.LX, out r.LY);
            Stick(p.RX, p.RY, o.Deadzone, out r.RX, out r.RY);
            return r;
        }

        private static void Stick(float x, float y, float dz, out short ox, out short oy)
        {
            float mag = (float)Math.Sqrt(x * x + y * y);
            if (mag <= dz || mag < 1e-6f) { ox = 0; oy = 0; return; }
            float scaled = Math.Min(1f, (mag - dz) / (1f - dz));
            float k = scaled / mag;
            ox = ToShort(x * k);
            oy = ToShort(y * k);
        }

        private static short ToShort(float v) => (short)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 32767f);
    }
}
