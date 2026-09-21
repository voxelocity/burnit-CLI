// The disc.
//
// Drawn as a half-block pixel grid (two vertical pixels per character cell) so the
// circle comes out round in a terminal's 1:2 cells. One effect carries the whole
// thing: the write head. It sits on the burn front at the current angle, with a
// short trail decaying behind it, and the front advances by *area* rather than
// radius -- which is how a disc actually fills up.

using System;

namespace Burnit
{
    public enum DiscMode { Idle, Writing, Erasing, Reading, Done }

    public static class DiscWidget
    {
        // Geometry, as fractions of the disc radius.
        // The rim is deliberately a couple of pixels wide: any thinner and it falls
        // below the sample grid and breaks up into stair-steps.
        private const double OuterEdge = 1.000;
        private const double RimInner  = 0.928;
        private const double DataOuter = 0.918;
        private const double DataInner = 0.300;
        private const double ClampOut  = 0.292;
        private const double ClampIn   = 0.150;
        private const double HubOut    = 0.148;
        private const double HoleR     = 0.104;

        public static void Render(Screen screen, int ox, int oy, int cols, int rows,
                                  Theme theme, DiscMode mode, double progress, double spin, double time)
        {
            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;

            int pw = cols;
            int ph = rows * 2;
            double cx = (pw - 1) / 2.0;
            double cy = (ph - 1) / 2.0;
            double radius = Math.Min(pw, ph) / 2.0 - 0.5;

            // Burn front advances with area so the visual pace matches the byte pace.
            double a0 = DataInner * DataInner;
            double a1 = DataOuter * DataOuter;
            double front = Math.Sqrt(a0 + progress * (a1 - a0));

            int[] px = new int[pw * ph];

            // 3x3 supersampling. Without it the rim stairsteps and the sub-pixel rings
            // around the hub break up into speckle.
            const int SS = 3;
            double[] off = new double[SS];
            for (int k = 0; k < SS; k++) off[k] = (k + 0.5) / SS - 0.5;

            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    int r = 0, g = 0, b = 0, hits = 0;
                    for (int sy = 0; sy < SS; sy++)
                    {
                        double dy = (y + off[sy] - cy) / radius;
                        for (int sx = 0; sx < SS; sx++)
                        {
                            double dx = (x + off[sx] - cx) / radius;
                            double u = Math.Sqrt(dx * dx + dy * dy);
                            int c = Shade(theme, mode, u, Math.Atan2(dy, dx), front, progress, spin, time);
                            if (c == Rgb.None) continue;
                            r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF;
                            hits++;
                        }
                    }

                    if (hits == 0) { px[y * pw + x] = Rgb.None; continue; }

                    int avg = Rgb.Make(r / hits, g / hits, b / hits);
                    int total = SS * SS;
                    // Partial coverage fades into the page rather than stairstepping.
                    px[y * pw + x] = hits == total ? avg : Rgb.Lerp(theme.Background, avg, (double)hits / total);
                }
            }

            // Pack pixel pairs into half-block cells.
            for (int ry = 0; ry < rows; ry++)
            {
                for (int x = 0; x < pw; x++)
                {
                    int top = px[(ry * 2) * pw + x];
                    int bot = px[(ry * 2 + 1) * pw + x];
                    int sx = ox + x, sy = oy + ry;

                    if (top == Rgb.None && bot == Rgb.None)
                        screen.Set(sx, sy, ' ', Rgb.None, theme.Background);
                    else if (top == Rgb.None)
                        screen.Set(sx, sy, '▄', bot, theme.Background);   // lower half
                    else if (bot == Rgb.None)
                        screen.Set(sx, sy, '▀', top, theme.Background);   // upper half
                    else
                        screen.Set(sx, sy, '▀', top, bot);
                }
            }
        }

        private static int Shade(Theme t, DiscMode mode, double u, double theta,
                                 double front, double progress, double spin, double time)
        {
            if (u > OuterEdge || u < HoleR) return Rgb.None;

            // Non-data furniture: outer rim, clamping area, hub ring.
            if (u >= RimInner) return Sheen(t.Rim, theta, spin, 0.22);
            if (u > DataOuter) return Rgb.Scale(t.Rim, 0.55);
            if (u < HubOut && u >= HoleR) return Sheen(t.Rim, theta, spin, 0.18);
            if (u < ClampIn) return Rgb.Scale(t.Rim, 0.45);
            if (u < DataInner)
            {
                // Mirror band between the clamping area and the data area.
                double k = u < ClampOut ? 0.38 : 0.62;
                return Sheen(Rgb.Scale(t.Rim, k), theta, spin, 0.20);
            }

            // ---- data area ----
            bool inside = u <= front;
            int dye;

            switch (mode)
            {
                case DiscMode.Erasing:
                    dye = inside ? t.Unwritten : t.Written;
                    break;
                case DiscMode.Reading:
                    dye = t.Written;
                    break;
                case DiscMode.Done:
                    dye = t.Written;
                    break;
                case DiscMode.Idle:
                    dye = t.Unwritten;
                    break;
                default:
                    dye = inside ? t.Written : t.Unwritten;
                    break;
            }

            // Concentric track banding. Kept well under the pixel Nyquist limit -- a
            // realistic track pitch just aliases into stripes at this resolution.
            double band = 1.0 + 0.045 * Math.Sin(u * 26.0);
            dye = Rgb.Scale(dye, band);
            dye = Sheen(dye, theta, spin, inside ? 0.10 : 0.16);

            if (mode == DiscMode.Idle) return dye;

            if (mode == DiscMode.Done)
            {
                double pulse = 0.10 + 0.05 * Math.Sin(time * 2.2);
                return Rgb.Lerp(dye, t.Hot, pulse);
            }

            // Residual warmth just behind the front, all the way round.
            double edge = Math.Abs(u - front);
            double warm = edge < 0.030 ? (1.0 - edge / 0.030) * 0.26 : 0.0;

            // The head itself: on the front, at the current angle, trailing behind.
            double radial = 1.0 - Math.Abs(u - front) / 0.055;
            double head = 0.0;
            if (radial > 0.0 && progress > 0.0005)
            {
                double behind = spin - theta;
                behind = behind % (Math.PI * 2);
                if (behind < 0) behind += Math.PI * 2;
                head = radial * Math.Exp(-behind / 0.42);
            }

            int hot = mode == DiscMode.Reading ? Rgb.Lerp(t.Hot, Rgb.Make(255, 255, 255), 0.45) : t.Hot;
            double heat = warm + head;
            if (heat > 1.0) heat = 1.0;
            return Rgb.Lerp(dye, hot, heat);
        }

        /// <summary>A broad soft highlight rotating with the disc, so it reads as spinning.</summary>
        private static int Sheen(int c, double theta, double spin, double strength)
        {
            double s = Math.Cos(theta - spin * 0.6);
            double k = 1.0 + strength * s * Math.Abs(s);
            return Rgb.Scale(c, k);
        }
    }
}
