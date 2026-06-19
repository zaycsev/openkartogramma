using System;
using System.Drawing;
using System.Windows.Forms;

namespace KartogrammaPlugin
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Диалог выбора ACI-цвета — полная палитра 255 цветов
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed class AciColorPickerForm : Form
    {
        public int SelectedAci { get; private set; }

        private PictureBox    _grid    = null!;
        private Panel         _preview = null!;
        private NumericUpDown _nudAci  = null!;
        private Label         _lblName = null!;

        // Размер одного квадратика
        private const int SwW = 16, SwH = 14, Gap = 1;
        // 24 цветовых тона (столбцы) × 11 строк (0 = стандарт, 1-10 = палитра)
        private const int Cols = 24, Rows = 11;

        public AciColorPickerForm(int currentAci)
        {
            SelectedAci = Math.Max(1, Math.Min(255, currentAci));
            BuildUI();
        }

        private void BuildUI()
        {
            Text            = "Выбор цвета (ACI)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterParent;
            Font            = new Font("Segoe UI", 8.5f);

            int gridW = Cols * (SwW + Gap) + 1;
            int gridH = Rows * (SwH + Gap) + 1;

            _grid = new PictureBox
            {
                Location    = new Point(8, 8),
                Size        = new Size(gridW, gridH),
                BorderStyle = BorderStyle.FixedSingle,
                Cursor      = Cursors.Hand
            };
            _grid.Paint      += OnGridPaint;
            _grid.MouseClick += OnGridClick;
            _grid.MouseMove  += OnGridHover;
            Controls.Add(_grid);

            int ctrlY = gridH + 14;

            Controls.Add(new Label { Text = "ACI:", Location = new Point(8, ctrlY + 4), AutoSize = true });
            _nudAci = new NumericUpDown
            {
                Minimum = 1, Maximum = 255, Value = SelectedAci,
                Location = new Point(36, ctrlY), Size = new Size(58, 22), DecimalPlaces = 0
            };
            _nudAci.ValueChanged += (s, e) => SelectAci((int)_nudAci.Value);
            Controls.Add(_nudAci);

            Controls.Add(new Label { Text = "Цвет:", Location = new Point(104, ctrlY + 4), AutoSize = true });
            _preview = new Panel
            {
                Location    = new Point(140, ctrlY),
                Size        = new Size(48, 22),
                BackColor   = AciToColor(SelectedAci),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_preview);

            _lblName = new Label
            {
                Text      = AciName(SelectedAci),
                Location  = new Point(198, ctrlY + 4),
                AutoSize  = true,
                ForeColor = Color.Gray
            };
            Controls.Add(_lblName);

            var btnOk = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK,
                Location = new Point(gridW - 106, ctrlY), Size = new Size(50, 26)
            };
            var btnCancel = new Button
            {
                Text = "Отмена", DialogResult = DialogResult.Cancel,
                Location = new Point(gridW - 52, ctrlY), Size = new Size(60, 26)
            };
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ClientSize   = new Size(gridW + 16, ctrlY + 36);
        }

        private void SelectAci(int aci)
        {
            SelectedAci        = Math.Max(1, Math.Min(255, aci));
            _nudAci.Value      = SelectedAci;
            _preview.BackColor = AciToColor(SelectedAci);
            _lblName.Text      = AciName(SelectedAci);
            _grid.Invalidate();
        }

        // ── Отрисовка ────────────────────────────────────────────────────────────
        private void OnGridPaint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(30, 30, 30));

            for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
            {
                int aci = AciFromCell(col, row);
                int x   = col * (SwW + Gap);
                int y   = row * (SwH + Gap);

                if (aci < 1)
                {
                    // Пустые ячейки строки 0 — тёмно-серый прямоугольник вместо чёрного провала
                    if (row == 0)
                    {
                        using var eb = new SolidBrush(Color.FromArgb(52, 52, 52));
                        g.FillRectangle(eb, x, y, SwW, SwH);
                    }
                    continue;
                }

                using (var br = new SolidBrush(AciToColor(aci)))
                    g.FillRectangle(br, x, y, SwW, SwH);

                if (aci == SelectedAci)
                    g.DrawRectangle(Pens.Yellow, x, y, SwW - 1, SwH - 1);
            }
        }

        private void OnGridClick(object? sender, MouseEventArgs e)
        {
            int aci = HitTest(e.X, e.Y);
            if (aci > 0) SelectAci(aci);
        }

        private void OnGridHover(object? sender, MouseEventArgs e)
        {
            int aci = HitTest(e.X, e.Y);
            if (aci > 0) _lblName.Text = AciName(aci);
        }

        // ── Раскладка ────────────────────────────────────────────────────────────
        // Строка 0: кол.0-8 = ACI 1-9 (стандарт), кол.9-14 = ACI 250-255 (серые),
        //           кол.15-23 = пастельные: светлые оттенки радуги (shade=1)
        // Строки 1-10: кол.= тон (0-23), строка-1 = оттенок (0-9) → ACI 10-249
        private static int AciFromCell(int col, int row)
        {
            if (row == 0)
            {
                if (col <  9) return col + 1;           // ACI 1-9
                if (col < 15) return 250 + (col - 9);   // ACI 250-255 (серые)
                // Пастели: светлый shade=1 равномерно по радуге (hue 0,2,4,6,8,12,16,20,22)
                int[] pastels = { 11, 31, 51, 71, 91, 131, 171, 211, 231 };
                return pastels[col - 15];
            }
            if (col < 24) return 10 + col * 10 + (row - 1);
            return -1;
        }

        private int HitTest(int mx, int my)
        {
            int col = mx / (SwW + Gap);
            int row = my / (SwH + Gap);
            if (col < 0 || col >= Cols || row < 0 || row >= Rows) return -1;
            return AciFromCell(col, row);
        }

        // ── ACI → Color ──────────────────────────────────────────────────────────
        internal static Color AciToColor(int aci)
        {
            aci = Math.Max(1, Math.Min(255, aci));
            return aci switch
            {
                1  => Color.FromArgb(255,   0,   0),
                2  => Color.FromArgb(255, 255,   0),
                3  => Color.FromArgb(  0, 255,   0),
                4  => Color.FromArgb(  0, 255, 255),
                5  => Color.FromArgb(  0,   0, 255),
                6  => Color.FromArgb(255,   0, 255),
                7  => Color.FromArgb(255, 255, 255),
                8  => Color.FromArgb( 65,  65,  65),
                9  => Color.FromArgb(128, 128, 128),
                >= 250 => GrayAci(aci),
                _      => PaletteAci(aci)
            };
        }

        private static Color GrayAci(int aci)
        {
            int g = (aci - 250) * 40 + 10;
            return Color.FromArgb(g, g, g);
        }

        // ACI 10-249: 24 тона × 10 оттенков (столбец = тон, строка = яркость)
        private static Color PaletteAci(int aci)
        {
            int idx   = aci - 10;
            int hue   = idx / 10;   // 0..23  → 0°..345°
            int shade = idx % 10;   // 0..9

            double h = hue * 15.0;
            double s, l;
            switch (shade)
            {
                case 0: s = 0.45; l = 0.92; break;
                case 1: s = 0.75; l = 0.82; break;
                case 2: s = 1.00; l = 0.72; break;
                case 3: s = 1.00; l = 0.62; break;
                case 4: s = 1.00; l = 0.50; break;  // чистый цвет
                case 5: s = 1.00; l = 0.40; break;
                case 6: s = 1.00; l = 0.30; break;
                case 7: s = 1.00; l = 0.20; break;
                case 8: s = 0.55; l = 0.50; break;
                default: s = 0.28; l = 0.55; break;
            }
            return HslToRgb(h, s, l);
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
            double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
            double m = l - c / 2.0;
            double r, g, b;
            if      (h <  60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else              { r = c; g = 0; b = x; }
            return Color.FromArgb(Clamp(r + m), Clamp(g + m), Clamp(b + m));
        }

        private static int Clamp(double v) =>
            Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));

        private static string AciName(int aci) => aci switch
        {
            1 => "ACI 1 — Красный",   2 => "ACI 2 — Жёлтый",
            3 => "ACI 3 — Зелёный",   4 => "ACI 4 — Голубой",
            5 => "ACI 5 — Синий",     6 => "ACI 6 — Пурпурный",
            7 => "ACI 7 — Белый",     8 => "ACI 8 — Тёмно-серый",
            9 => "ACI 9 — Серый",
            >= 250 => $"ACI {aci} — Серый",
            _ => $"ACI {aci}"
        };
    }
}
