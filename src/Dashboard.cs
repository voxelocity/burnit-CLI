// Live burn dashboard: the disc on the left, hard numbers on the right.
//
// The render thread runs independently of IMAPI2's progress callbacks, which only
// arrive once or twice a second. The disc keeps spinning between them; only the
// burn front waits for real data.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Burnit
{
    public sealed class BurnState
    {
        private readonly object _lock = new object();

        public string Operation = "burn";
        public string DriveText = "";
        public string MediaText = "";
        public string SpeedText = "";
        public string Detail = "";
        public bool DryRun;
        public DiscMode Mode = DiscMode.Idle;

        public string[] Steps = new string[0];
        public int StepIndex = -1;

        public long Done;
        public long Total;
        public int BufferUsed;
        public int BufferTotal;
        public int ReportedElapsed = -1;
        public int ReportedRemaining = -1;

        public readonly DateTime Started = DateTime.UtcNow;
        private readonly List<long> _sampleTicks = new List<long>();
        private readonly List<long> _sampleBytes = new List<long>();
        private double _rate;

        public void SetProgress(long done, long total)
        {
            lock (_lock)
            {
                Done = done;
                if (total > 0) Total = total;

                long now = DateTime.UtcNow.Ticks;
                _sampleTicks.Add(now);
                _sampleBytes.Add(done);
                // Keep roughly the last 5 seconds of samples.
                while (_sampleTicks.Count > 2 && now - _sampleTicks[0] > 5L * TimeSpan.TicksPerSecond)
                {
                    _sampleTicks.RemoveAt(0);
                    _sampleBytes.RemoveAt(0);
                }
                if (_sampleTicks.Count >= 2)
                {
                    double secs = (_sampleTicks[_sampleTicks.Count - 1] - _sampleTicks[0]) / (double)TimeSpan.TicksPerSecond;
                    long bytes = _sampleBytes[_sampleBytes.Count - 1] - _sampleBytes[0];
                    if (secs > 0.4 && bytes >= 0) _rate = bytes / secs;
                }
            }
        }

        public double Rate { get { lock (_lock) { return _rate; } } }

        public double Fraction
        {
            get
            {
                lock (_lock)
                {
                    if (Total <= 0) return 0;
                    double f = (double)Done / Total;
                    return f < 0 ? 0 : (f > 1 ? 1 : f);
                }
            }
        }

        public void Step(string name)
        {
            lock (_lock)
            {
                for (int i = 0; i < Steps.Length; i++)
                {
                    if (string.Equals(Steps[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (i > StepIndex) StepIndex = i;
                        return;
                    }
                }
            }
        }
    }

    public static class Fmt
    {
        public static string Bytes(long n)
        {
            if (n < 0) return "-";
            double v = n;
            string[] units = { "B", "KiB", "MiB", "GiB" };
            int u = 0;
            while (v >= 1024.0 && u < units.Length - 1) { v /= 1024.0; u++; }
            string num = u == 0 ? v.ToString("0", CultureInfo.InvariantCulture)
                                : v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
            return num + " " + units[u];
        }

        public static string Rate(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "--";
            double v = bytesPerSec / (1000.0 * 1000.0);
            if (v >= 10) return v.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (v >= 1) return v.ToString("0.00", CultureInfo.InvariantCulture) + " MB/s";
            return (bytesPerSec / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kB/s";
        }

        public static string Time(int seconds)
        {
            if (seconds < 0) return "--:--";
            if (seconds >= 3600)
                return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + ":"
                     + ((seconds / 60) % 60).ToString("00", CultureInfo.InvariantCulture) + ":"
                     + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
            return (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
                 + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        public static string Ellipsis(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            if (max <= 1) return s.Substring(0, max);
            return "…" + s.Substring(s.Length - (max - 1));
        }
    }

    public sealed class Dashboard : IDisposable
    {
        private readonly BurnState _state;
        private readonly Theme _theme;
        private readonly bool _fancy;
        private Screen _screen;
        private Thread _thread;
        private volatile bool _running;
        private double _spin;
        private readonly DateTime _t0 = DateTime.UtcNow;

        private int _discCols, _discRows, _panelX, _boxW, _boxH;
        private long _lastPlainPrint;

        public Dashboard(BurnState state, Theme theme, bool fancy)
        {
            _state = state;
            _theme = theme;
            _fancy = fancy;
        }

        public void Start()
        {
            if (_fancy)
            {
                int w, h;
                Native.GetConsoleSize(out w, out h);
                _boxW = Math.Min(80, Math.Max(52, w));
                _discRows = Math.Max(8, Math.Min(20, h - 9));
                _discCols = _discRows * 2;
                if (2 + _discCols + 3 + 24 > _boxW)
                {
                    _discCols = Math.Max(16, _boxW - 2 - 3 - 24);
                    _discRows = Math.Max(8, _discCols / 2);
                    _discCols = _discRows * 2;
                }
                _panelX = 2 + _discCols + 3;
                _boxH = _discRows + 7;
                _screen = new Screen(_boxW, _boxH);

                Console.Out.Write(Ansi.AltScreen);
                Console.Out.Write(Ansi.HideCursor);
                Console.Out.Write(Ansi.ClearScreen);
            }

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null)
            {
                try { _thread.Join(600); }
                catch (Exception) { }
                _thread = null;
            }
            if (_fancy && _screen != null)
            {
                RenderFrame();
                Thread.Sleep(90);
                Console.Out.Write(Ansi.Reset);
                Console.Out.Write(Ansi.ShowCursor);
                Console.Out.Write(Ansi.MainScreen);
                Console.Out.Flush();
            }
        }

        public void Dispose() { Stop(); }

        private void Loop()
        {
            while (_running)
            {
                if (_fancy) RenderFrame(); else RenderPlain();
                Thread.Sleep(_fancy ? 45 : 150);
            }
        }

        // ---- plain output ---------------------------------------------------------

        private void RenderPlain()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - _lastPlainPrint < 8L * TimeSpan.TicksPerSecond / 10) return;
            _lastPlainPrint = now;

            double f = _state.Fraction;
            string line = (f * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%  "
                + _state.Detail + "  "
                + Fmt.Bytes(_state.Done) + " / " + Fmt.Bytes(_state.Total)
                + "  " + Fmt.Rate(_state.Rate)
                + "  eta " + Fmt.Time(Remaining());
            Console.WriteLine(line);
        }

        private int Remaining()
        {
            if (_state.ReportedRemaining >= 0) return _state.ReportedRemaining;
            double rate = _state.Rate;
            if (rate <= 1 || _state.Total <= 0) return -1;
            long left = _state.Total - _state.Done;
            if (left < 0) left = 0;
            return (int)(left / rate);
        }

        private int Elapsed()
        {
            if (_state.ReportedElapsed >= 0) return _state.ReportedElapsed;
            return (int)(DateTime.UtcNow - _state.Started).TotalSeconds;
        }

        // ---- full screen ----------------------------------------------------------

        private void RenderFrame()
        {
            double t = (DateTime.UtcNow - _t0).TotalSeconds;
            DiscMode mode = _state.Mode;
            if (mode == DiscMode.Writing || mode == DiscMode.Erasing || mode == DiscMode.Reading)
                _spin += 0.30;
            else
                _spin += 0.07;

            Theme th = _theme;
            _screen.Clear(th.Background);

            DrawBox(t);
            DiscWidget.Render(_screen, 2, 2, _discCols, _discRows, th, mode, _state.Fraction, _spin, t);
            DrawPanel();
            DrawSteps();
            DrawProgressBar();

            _screen.Flush(Console.Out);
        }

        private void DrawBox(double t)
        {
            Theme th = _theme;
            int w = _boxW, h = _boxH;

            string title = " BURNIT " + "·" + " " + _state.Operation + " ";
            if (_state.DryRun) title += "· DRY RUN ";

            StringBuilder top = new StringBuilder();
            top.Append('╭');
            top.Append('─');
            top.Append(title);
            while (top.Length < w - 1) top.Append('─');
            top.Length = w - 1;
            top.Append('╮');
            _screen.Text(0, 0, top.ToString(), th.Panel, th.Background);

            // Re-colour the title so it reads as the one accent on the frame.
            _screen.Text(2, 0, title, th.Accent, th.Background);
            if (_state.DryRun)
            {
                int at = title.IndexOf("DRY RUN", StringComparison.Ordinal);
                if (at >= 0) _screen.Text(2 + at, 0, "DRY RUN", Rgb.Make(232, 196, 84), th.Background);
            }

            for (int y = 1; y < h - 1; y++)
            {
                _screen.Set(0, y, '│', th.Panel, th.Background);
                _screen.Set(w - 1, y, '│', th.Panel, th.Background);
            }

            StringBuilder bot = new StringBuilder();
            bot.Append('╰');
            while (bot.Length < w - 1) bot.Append('─');
            bot.Append('╯');
            _screen.Text(0, h - 1, bot.ToString(), th.Panel, th.Background);

            string hint = " ctrl-c aborts ";
            _screen.Text(w - 2 - hint.Length, h - 1, hint, th.Dim, th.Background);
        }

        private void DrawPanel()
        {
            Theme th = _theme;
            int x = _panelX;
            int vw = _boxW - 1 - x;
            int y = 2;
            int limit = _boxH - 5;      // keep clear of the steps line

            Field(x, y++, "DRIVE", Fmt.Ellipsis(_state.DriveText, vw - 9), vw);
            Field(x, y++, "MEDIA", Fmt.Ellipsis(_state.MediaText, vw - 9), vw);
            Field(x, y++, "SPEED", Fmt.Ellipsis(_state.SpeedText, vw - 9), vw);
            y++;

            string wrote = Fmt.Bytes(_state.Done) + " / " + Fmt.Bytes(_state.Total);
            Field(x, y++, _state.Mode == DiscMode.Reading ? "READ" : "WROTE", wrote, vw);
            Field(x, y++, "RATE", Fmt.Rate(_state.Rate), vw);
            Field(x, y++, "ELAPSED", Fmt.Time(Elapsed()), vw);
            Field(x, y++, "REMAIN", Fmt.Time(Remaining()), vw);

            if (_state.BufferTotal > 0 && y + 1 < limit)
            {
                y++;
                int pct = (int)(100.0 * _state.BufferUsed / _state.BufferTotal);
                if (pct > 100) pct = 100;
                if (pct < 0) pct = 0;
                _screen.Text(x, y, "BUFFER", th.Label, th.Background);
                int cells = Math.Min(10, Math.Max(4, vw - 14));
                int on = (int)Math.Round(cells * pct / 100.0);
                StringBuilder b = new StringBuilder();
                for (int i = 0; i < cells; i++) b.Append(i < on ? '▮' : '▯');
                // A starved buffer is the one number worth colouring red.
                int c = pct < 25 ? Rgb.Make(214, 96, 72) : th.Accent;
                _screen.Text(x + 8, y, b.ToString(), c, th.Background);
                _screen.Text(x + 9 + cells, y, pct.ToString(CultureInfo.InvariantCulture) + "%", th.Value, th.Background);
            }
        }

        private void Field(int x, int y, string label, string value, int vw)
        {
            if (y >= _boxH - 5) return;     // the steps line owns the bottom
            _screen.Text(x, y, label, _theme.Label, _theme.Background);
            _screen.Text(x + 8, y, Fmt.Ellipsis(value, Math.Max(1, vw - 8)), _theme.Value, _theme.Background);
        }

        private void DrawSteps()
        {
            Theme th = _theme;
            string[] steps = _state.Steps;
            if (steps == null || steps.Length == 0) return;

            int y = _boxH - 4;
            int x = 2;
            int idx = _state.StepIndex;
            for (int i = 0; i < steps.Length; i++)
            {
                char glyph = i < idx ? '●' : (i == idx ? '◐' : '○');
                int c = i < idx ? th.Dim : (i == idx ? th.Accent : th.Dim);
                if (x + steps[i].Length + 3 >= _boxW - 1) break;
                _screen.Set(x, y, glyph, c, th.Background);
                _screen.Text(x + 2, y, steps[i], i == idx ? th.Value : th.Dim, th.Background);
                x += steps[i].Length + 4;
            }
        }

        private void DrawProgressBar()
        {
            Theme th = _theme;
            int y = _boxH - 3;
            int x = 2;
            int pctW = 7;
            int barW = _boxW - 1 - x - pctW - 1;
            if (barW < 8) return;

            double f = _state.Fraction;
            double exact = f * barW;
            int full = (int)exact;
            double frac = exact - full;

            const string eighths = "▏▎▍▌▋▊▉";

            for (int i = 0; i < barW; i++)
            {
                char ch;
                int fg;
                if (i < full) { ch = '█'; fg = th.Accent; }
                else if (i == full && frac > 0.06)
                {
                    int k = (int)(frac * 8);
                    if (k > 6) k = 6;
                    ch = eighths[k];
                    fg = th.Accent;
                }
                else { ch = '─'; fg = Rgb.Scale(th.Panel, 0.85); }
                _screen.Set(x + i, y, ch, fg, th.Background);
            }

            string pct = (f * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            _screen.Text(_boxW - 1 - pct.Length - 1, y, pct, th.Value, th.Background);

            string detail = _state.Detail;
            if (!string.IsNullOrEmpty(detail))
                _screen.Text(2, _boxH - 2, Fmt.Ellipsis(detail, _boxW - 4), th.Dim, th.Background);
        }
    }
}
