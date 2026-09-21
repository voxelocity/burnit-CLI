// A tiny double-buffered truecolor terminal surface.
//
// Frames are composed into a cell grid and flushed as a per-row diff, so only the
// part of the screen that actually moved gets written. That keeps the disc
// animation smooth without ever clearing the screen (which is what causes flicker).

using System;
using System.Text;

namespace Burnit
{
    public static class Ansi
    {
        public const string Reset = "\x1b[0m";
        public const string HideCursor = "\x1b[?25l";
        public const string ShowCursor = "\x1b[?25h";
        public const string AltScreen = "\x1b[?1049h";
        public const string MainScreen = "\x1b[?1049l";
        public const string ClearScreen = "\x1b[2J\x1b[H";

        public static string Fg(int rgb)
        {
            return "\x1b[38;2;" + ((rgb >> 16) & 0xFF) + ";" + ((rgb >> 8) & 0xFF) + ";" + (rgb & 0xFF) + "m";
        }

        public static string Bg(int rgb)
        {
            return "\x1b[48;2;" + ((rgb >> 16) & 0xFF) + ";" + ((rgb >> 8) & 0xFF) + ";" + (rgb & 0xFF) + "m";
        }
    }

    public static class Rgb
    {
        public const int None = -1;

        public static int Make(int r, int g, int b)
        {
            if (r < 0) r = 0; if (r > 255) r = 255;
            if (g < 0) g = 0; if (g > 255) g = 255;
            if (b < 0) b = 0; if (b > 255) b = 255;
            return (r << 16) | (g << 8) | b;
        }

        public static int Lerp(int a, int b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            int ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
            int br = (b >> 16) & 0xFF, bg = (b >> 8) & 0xFF, bb = b & 0xFF;
            return Make((int)(ar + (br - ar) * t + 0.5),
                        (int)(ag + (bg - ag) * t + 0.5),
                        (int)(ab + (bb - ab) * t + 0.5));
        }

        public static int Scale(int c, double k)
        {
            return Make((int)(((c >> 16) & 0xFF) * k), (int)(((c >> 8) & 0xFF) * k), (int)((c & 0xFF) * k));
        }

        /// <summary>h wraps at 1.0; s and v are 0..1.</summary>
        public static int FromHsv(double h, double s, double v)
        {
            h = h - Math.Floor(h);
            if (s < 0) s = 0; if (s > 1) s = 1;
            if (v < 0) v = 0; if (v > 1) v = 1;

            double sector = h * 6.0;
            int i = (int)Math.Floor(sector);
            double f = sector - i;
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Make((int)(r * 255 + 0.5), (int)(g * 255 + 0.5), (int)(b * 255 + 0.5));
        }

        /// <summary>Cheap deterministic hash, for sparkle placement that does not flicker randomly.</summary>
        public static double Hash(int x, int y, int z)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + z * 1442695040;
                h = (h ^ (h >> 13)) * 1274126177;
                h = h ^ (h >> 16);
                return (h & 0x7FFFFFF) / (double)0x7FFFFFF;
            }
        }
    }

    public sealed class Screen
    {
        public readonly int Width;
        public readonly int Height;

        private readonly char[] _ch, _pch;
        private readonly int[] _fg, _bg, _pfg, _pbg;
        private readonly StringBuilder _sb = new StringBuilder(1 << 16);
        private bool _firstFlush = true;

        public Screen(int width, int height)
        {
            Width = width;
            Height = height;
            int n = width * height;
            _ch = new char[n]; _pch = new char[n];
            _fg = new int[n]; _bg = new int[n];
            _pfg = new int[n]; _pbg = new int[n];
            for (int i = 0; i < n; i++)
            {
                _ch[i] = ' '; _pch[i] = '\0';
                _fg[i] = Rgb.None; _bg[i] = Rgb.None;
                _pfg[i] = -2; _pbg[i] = -2;
            }
        }

        public void Clear(int bg)
        {
            for (int i = 0; i < _ch.Length; i++)
            {
                _ch[i] = ' ';
                _fg[i] = Rgb.None;
                _bg[i] = bg;
            }
        }

        public void Set(int x, int y, char c, int fg, int bg)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int i = y * Width + x;
            _ch[i] = c; _fg[i] = fg; _bg[i] = bg;
        }

        /// <summary>What is currently in a cell, so overlays can avoid clobbering it.</summary>
        public char CharAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return '\0';
            return _ch[y * Width + x];
        }

        public void Text(int x, int y, string s, int fg)
        {
            Text(x, y, s, fg, Rgb.None);
        }

        public void Text(int x, int y, string s, int fg, int bg)
        {
            if (s == null) return;
            if (y < 0 || y >= Height) return;
            for (int i = 0; i < s.Length; i++)
            {
                int xx = x + i;
                if (xx >= Width) break;
                if (xx < 0) continue;
                int k = y * Width + xx;
                _ch[k] = s[i];
                _fg[k] = fg;
                if (bg != Rgb.None) _bg[k] = bg;
            }
        }

        /// <summary>Writes the diff between this frame and the last one.</summary>
        public void Flush(System.IO.TextWriter output)
        {
            _sb.Length = 0;
            int curFg = -2, curBg = -2;

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                int first = -1, last = -1;
                for (int x = 0; x < Width; x++)
                {
                    int i = row + x;
                    if (_firstFlush || _ch[i] != _pch[i] || _fg[i] != _pfg[i] || _bg[i] != _pbg[i])
                    {
                        if (first < 0) first = x;
                        last = x;
                    }
                }
                if (first < 0) continue;

                _sb.Append("\x1b[").Append(y + 1).Append(';').Append(first + 1).Append('H');
                for (int x = first; x <= last; x++)
                {
                    int i = row + x;
                    if (_fg[i] != curFg)
                    {
                        curFg = _fg[i];
                        _sb.Append(curFg == Rgb.None ? "\x1b[39m" : Ansi.Fg(curFg));
                    }
                    if (_bg[i] != curBg)
                    {
                        curBg = _bg[i];
                        _sb.Append(curBg == Rgb.None ? "\x1b[49m" : Ansi.Bg(curBg));
                    }
                    _sb.Append(_ch[i]);
                }
            }

            if (_sb.Length > 0)
            {
                _sb.Append(Ansi.Reset);
                output.Write(_sb.ToString());
                output.Flush();
            }

            Array.Copy(_ch, _pch, _ch.Length);
            Array.Copy(_fg, _pfg, _fg.Length);
            Array.Copy(_bg, _pbg, _bg.Length);
            _firstFlush = false;
        }
    }

    public sealed class Theme
    {
        public string Name;
        public int Background;   // page background
        public int Panel;        // frame / rules
        public int Label;        // field labels
        public int Value;        // field values
        public int Dim;          // de-emphasised text
        public int Rim;          // disc outer edge + hub
        public int Unwritten;    // untouched dye
        public int Written;      // burned dye
        public int Hot;          // the write spot itself
        public int Accent;       // headings, the one colour that carries the eye

        /// <summary>Opt-in chaos. Off everywhere unless --kawaii is passed.</summary>
        public bool Rave;

        public static Theme Get(string name)
        {
            switch ((name ?? "amber").ToLowerInvariant())
            {
                case "ice": return Ice();
                case "mono": return Mono();
                case "kawaii":
                case "rave": return Kawaii();
                default: return Amber();
            }
        }

        public static Theme Amber()
        {
            Theme t = new Theme();
            t.Name = "amber";
            t.Background = Rgb.Make(12, 12, 14);
            t.Panel = Rgb.Make(62, 62, 70);
            t.Label = Rgb.Make(118, 118, 128);
            t.Value = Rgb.Make(222, 222, 226);
            t.Dim = Rgb.Make(88, 88, 96);
            t.Rim = Rgb.Make(126, 130, 138);
            t.Unwritten = Rgb.Make(46, 50, 58);
            t.Written = Rgb.Make(132, 68, 20);
            t.Hot = Rgb.Make(255, 206, 122);
            t.Accent = Rgb.Make(226, 146, 46);
            return t;
        }

        public static Theme Ice()
        {
            Theme t = Amber();
            t.Name = "ice";
            t.Unwritten = Rgb.Make(44, 50, 60);
            t.Written = Rgb.Make(26, 96, 132);
            t.Hot = Rgb.Make(168, 234, 255);
            t.Accent = Rgb.Make(96, 178, 222);
            return t;
        }

        public static Theme Mono()
        {
            Theme t = Amber();
            t.Name = "mono";
            t.Unwritten = Rgb.Make(46, 46, 46);
            t.Written = Rgb.Make(104, 104, 104);
            t.Hot = Rgb.Make(240, 240, 240);
            t.Accent = Rgb.Make(198, 198, 198);
            return t;
        }

        public static Theme Kawaii()
        {
            Theme t = new Theme();
            t.Name = "kawaii";
            t.Rave = true;
            t.Background = Rgb.Make(14, 8, 22);
            t.Panel = Rgb.Make(186, 96, 200);
            t.Label = Rgb.Make(126, 220, 232);
            t.Value = Rgb.Make(255, 236, 250);
            t.Dim = Rgb.Make(124, 92, 150);
            t.Rim = Rgb.Make(226, 168, 244);
            t.Unwritten = Rgb.Make(38, 24, 56);
            t.Written = Rgb.Make(255, 96, 190);
            t.Hot = Rgb.Make(255, 255, 255);
            t.Accent = Rgb.Make(255, 130, 208);
            return t;
        }
    }
}
