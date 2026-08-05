using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KartogrammaPlugin
{
    /// <summary>Тип линий чертежа: имя и штриховой рисунок для предпросмотра.</summary>
    public sealed class LinetypeInfo
    {
        public string   Name   = "Continuous";
        /// <summary>Длины из LinetypeTableRecord: + штрих, − пробел, 0 точка.</summary>
        public double[] Dashes = Array.Empty<double>();
    }

    /// <summary>
    /// Диалог настройки штриховки картограммы и линии нулевых работ.
    /// Открывается кнопкой-кисточкой из раздела «Штриховка» главного окна.
    /// Размер и расположение кнопок совпадают с главным окном и с диалогом
    /// слоёв — по образцу LayerSettingsForm.
    /// </summary>
    public sealed class HatchSettingsForm : Form
    {
        // Результат — копии, исходные настройки меняются только по «ОК».
        public HatchSpec    CutStyle  { get; }
        public HatchSpec    FillStyle { get; }
        public ZeroLineStyle ZeroStyle { get; }

        private readonly IList<LinetypeInfo> _linetypes;
        /// <summary>Размер ячейки сетки — окошки предпросмотра показывают ровно
        /// одну ячейку по ширине, чтобы густота совпадала с чертежом.</summary>
        private readonly double _cellSize;

        private ListBox  _lstCut = null!,  _lstFill = null!;
        private Panel    _pnlCutColor = null!, _pnlFillColor = null!, _pnlZeroColor = null!;
        private NumericUpDown _nudCutAngle = null!, _nudCutScale = null!;
        private NumericUpDown _nudFillAngle = null!, _nudFillScale = null!;
        private Panel    _prvCut = null!, _prvFill = null!, _prvZero = null!;
        private ComboBox _cmbLineType = null!, _cmbWeight = null!;

        public HatchSettingsForm(HatchSpec cut, HatchSpec fill, ZeroLineStyle zero,
            IList<LinetypeInfo> linetypes, double cellSize, Size mainSize)
        {
            CutStyle   = cut.Clone();
            FillStyle  = fill.Clone();
            ZeroStyle  = zero.Clone();
            _cellSize  = cellSize > 1e-6 ? cellSize : 1.0;
            _linetypes = linetypes.Count > 0
                ? linetypes
                : new List<LinetypeInfo> { new LinetypeInfo() };

            Text            = "Картограмма — штриховка и линия нулевых работ";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = false;
            MinimizeBox     = false;
            AutoScaleMode   = AutoScaleMode.None;   // масштабируем вручную, как главное окно
            ClientSize      = mainSize;             // mainSize приходит уже в пикселях экрана
            Font            = new Font("Segoe UI", 9f);

            // Тот же коэффициент, что и в MainKartogrammaForm: шрифт задан в
            // пунктах и растёт вместе с DPI, поэтому пиксельные размеры обязаны
            // расти вместе с ним — иначе подписи вылезают за свои рамки.
            using (var g = CreateGraphics()) { _dpiScale = g.DpiX / 96f; }

            BuildUI(mainSize);
        }

        // ── DPI и обмер текста ────────────────────────────────────────────────

        /// <summary>1.0 при 96 DPI, 1.5 при 144 DPI и т.д.</summary>
        private float _dpiScale = 1f;
        private int S(int v) => (int)Math.Round(v * _dpiScale);

        /// <summary>Фактический размер строки данным шрифтом — ровно с теми же
        /// отступами, с какими её нарисует Label (без NoPadding: иначе замер
        /// уже отрисовки и последнюю букву срезает). Верстаем по нему, а не по
        /// «на глаз» подобранным константам: тогда макет переживает и другой
        /// DPI, и увеличенный размер текста в настройках Windows.</summary>
        private static Size TextSize(string text, Font f)
            => TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
                                        TextFormatFlags.SingleLine);

        /// <summary>Подпись по ширине своего текста — обрезать нечего.
        /// Размер ставим сразу (а не через AutoSize): по нему тут же считается
        /// положение соседних полей, и он обязан быть верным в этот момент.</summary>
        private Label Lbl(string text, Font f, int x, int y)
        {
            var sz = TextSize(text, f);
            return new Label
            {
                Text      = text,
                Font      = f,
                AutoSize  = false,
                Location  = new Point(x, y),
                Size      = new Size(sz.Width + S(2), sz.Height),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        /// <summary>Высота шапки GroupBox — ниже неё можно ставить содержимое.</summary>
        private int HeaderHeight(Font bold) => TextSize("Ауj", bold).Height + S(10);

        /// <summary>Выровнять элементы по вертикали внутри строки высотой rowH.</summary>
        private static void PlaceRow(int rowTop, int rowH, params Control[] items)
        {
            foreach (var c in items)
                c.Top = rowTop + Math.Max(0, (rowH - c.Height) / 2);
        }

        private void BuildUI(Size mainSize)
        {
            int pad  = S(8);
            var body = new Font("Segoe UI", 9f);
            var bold = new Font("Segoe UI", 9f, FontStyle.Bold);

            int btnH = Math.Max(S(28), TextSize("Ауj", body).Height + S(10));
            int btnY = mainSize.Height - btnH - S(14);

            // ── Линия нулевых работ ───────────────────────────────────────────
            var grpZero = new GroupBox
            {
                Text     = "Линия нулевых работ",
                Location = new Point(pad, pad),
                Size     = new Size(mainSize.Width - 2 * pad, S(82)),  // высоту уточним по строке
                Font     = bold
            };
            Controls.Add(grpZero);

            int zrowTop = HeaderHeight(bold) + S(4);
            int zx      = S(14);

            // Подписи меряем заранее и делим по ним остаток ширины: строка
            // должна умещаться в рамку и при крупном шрифте (в Windows размер
            // текста задаётся отдельно от DPI), а не уезжать за край окна.
            var lblZColor  = Lbl("Цвет:",      body, zx, zrowTop);
            var lblZType   = Lbl("Тип линии:", body, zx, zrowTop);
            var lblZWeight = Lbl("Вес:",       body, zx, zrowTop);
            var lblZPrv    = Lbl("Образец:",   body, zx, zrowTop);

            int zColorW = S(46);
            int zFixed  = S(14) + lblZColor.Width + S(6) + zColorW + S(16)
                        + lblZType.Width  + S(6) + S(16)
                        + lblZWeight.Width + S(6) + S(16)
                        + lblZPrv.Width   + S(6) + S(14);
            int zFree   = Math.Max(S(180), grpZero.ClientSize.Width - zFixed);

            int zTypeW   = Math.Max(S(110), zFree * 42 / 100);
            int zWeightW = Math.Max(S(80),  zFree * 24 / 100);
            // Образец — второстепенный: если места на него не осталось, прячем
            // его вместе с подписью, но поля выбора не режем.
            int zPrvW    = zFree - zTypeW - zWeightW;
            bool showPrv = zPrvW >= S(60);

            grpZero.Controls.Add(lblZColor);
            zx += lblZColor.Width + S(6);

            _pnlZeroColor = ColorPanel(ZeroStyle.ColorAci, new Point(zx, zrowTop),
                                       zColorW, Math.Max(S(24), lblZColor.Height + S(4)));
            _pnlZeroColor.Click += (s, e) =>
            {
                if (PickAci(ZeroStyle.ColorAci, out int aci))
                {
                    ZeroStyle.ColorAci = aci;
                    _pnlZeroColor.BackColor = AciColorPickerForm.AciToColor(aci);
                    _prvZero.Invalidate();
                }
            };
            grpZero.Controls.Add(_pnlZeroColor);
            zx += _pnlZeroColor.Width + S(16);

            lblZType.Left = zx;
            grpZero.Controls.Add(lblZType);
            zx += lblZType.Width + S(6);

            _cmbLineType = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(zx, zrowTop),
                Width         = zTypeW,   // высоту ComboBox назначает себе сам по шрифту
                Font          = body
            };
            foreach (var lt in _linetypes) _cmbLineType.Items.Add(lt.Name);
            SelectByText(_cmbLineType, ZeroStyle.LineType);
            _cmbLineType.SelectedIndexChanged += (s, e) =>
            {
                ZeroStyle.LineType = _cmbLineType.SelectedItem?.ToString() ?? "Continuous";
                _prvZero.Invalidate();
            };
            grpZero.Controls.Add(_cmbLineType);
            zx += _cmbLineType.Width + S(16);

            lblZWeight.Left = zx;
            grpZero.Controls.Add(lblZWeight);
            zx += lblZWeight.Width + S(6);

            _cmbWeight = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(zx, zrowTop),
                Width         = zWeightW,
                Font          = body
            };
            foreach (var w in ZeroLineStyle.StandardWeights)
                _cmbWeight.Items.Add(ZeroLineStyle.WeightText(w));
            _cmbWeight.SelectedIndex = Math.Max(0,
                Array.IndexOf(ZeroLineStyle.StandardWeights, ZeroStyle.LineWeight));
            _cmbWeight.SelectedIndexChanged += (s, e) =>
            {
                int i = _cmbWeight.SelectedIndex;
                if (i >= 0 && i < ZeroLineStyle.StandardWeights.Length)
                    ZeroStyle.LineWeight = ZeroLineStyle.StandardWeights[i];
                _prvZero.Invalidate();
            };
            grpZero.Controls.Add(_cmbWeight);
            zx += _cmbWeight.Width + S(16);

            lblZPrv.Left    = zx;
            lblZPrv.Visible = showPrv;
            grpZero.Controls.Add(lblZPrv);
            zx += lblZPrv.Width + S(6);

            int zprvH = Math.Max(S(30), _cmbLineType.Height + S(4));
            _prvZero = PreviewPanel(new Point(zx, zrowTop),
                                    new Size(Math.Max(S(20), zPrvW), zprvH));
            _prvZero.Visible = showPrv;
            _prvZero.Paint += PaintZeroPreview;
            grpZero.Controls.Add(_prvZero);

            // Высота строки — по самому высокому элементу, всё центрируем по ней.
            int zrowH = Math.Max(Math.Max(_cmbLineType.Height, _cmbWeight.Height),
                                 Math.Max(_pnlZeroColor.Height, zprvH));
            PlaceRow(zrowTop, zrowH, lblZColor, _pnlZeroColor, lblZType, _cmbLineType,
                     lblZWeight, _cmbWeight, lblZPrv, _prvZero);
            grpZero.Height = zrowTop + zrowH + S(12);

            // ── Две штриховки рядом ───────────────────────────────────────────
            int grpTop = grpZero.Bottom + pad;
            int grpH   = btnY - grpTop - pad;
            int halfW  = (mainSize.Width - 2 * pad - pad) / 2;

            var grpCut = BuildHatchGroup("Штриховка выемки", CutStyle,
                new Point(pad, grpTop), new Size(halfW, grpH),
                out _lstCut, out _pnlCutColor, out _nudCutAngle, out _nudCutScale, out _prvCut);
            Controls.Add(grpCut);

            var grpFill = BuildHatchGroup("Штриховка насыпи", FillStyle,
                new Point(pad + halfW + pad, grpTop), new Size(halfW, grpH),
                out _lstFill, out _pnlFillColor, out _nudFillAngle, out _nudFillScale, out _prvFill);
            Controls.Add(grpFill);

            // ── Кнопки ────────────────────────────────────────────────────────
            // Ширина — по самой длинной надписи, чтобы «Закрыть» не обрезалось.
            int closeW = Math.Max(S(100), TextSize("Закрыть", Font).Width + S(28));

            var btnOk = new Button
            {
                Text = "ОК", Location = new Point(pad, btnY),
                Size = new Size(closeW, btnH), DialogResult = DialogResult.OK
            };
            Controls.Add(btnOk);

            var btnClose = new Button
            {
                Text = "Закрыть", Location = new Point(mainSize.Width - closeW - pad, btnY),
                Size = new Size(closeW, btnH), DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnClose);

            // Откуда взялась палитра — полезно, когда образцов меньше ожидаемого
            var srcFont = new Font("Segoe UI", 8.5f);
            var lblSrc = new Label
            {
                Text      = HatchPatternCatalog.SourceFile != null
                    ? "Палитра образцов: " + System.IO.Path.GetFileName(HatchPatternCatalog.SourceFile)
                    : "Палитра образцов: встроенный набор (файл AutoCAD не найден)",
                Size      = new Size(mainSize.Width - 2 * (pad + closeW + pad),
                                     TextSize("Ауj", srcFont).Height + S(4)),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText,
                Font      = srcFont
            };
            lblSrc.Location = new Point(pad + closeW + pad,
                                        btnY + Math.Max(0, (btnH - lblSrc.Height) / 2));
            if (HatchPatternCatalog.SourceFile != null)
                new ToolTip().SetToolTip(lblSrc, HatchPatternCatalog.SourceFile);
            Controls.Add(lblSrc);

            AcceptButton = btnOk;
            CancelButton = btnClose;
        }

        /// <summary>Группа настройки одной штриховки: список образцов слева,
        /// параметры и крупный предпросмотр справа.</summary>
        private GroupBox BuildHatchGroup(string title, HatchSpec style,
            Point loc, Size size,
            out ListBox lst, out Panel pnlColor,
            out NumericUpDown nudAngle, out NumericUpDown nudScale, out Panel preview)
        {
            var body = new Font("Segoe UI", 9f);
            var bold = new Font("Segoe UI", 9f, FontStyle.Bold);
            var grp = new GroupBox
            {
                Text     = title,
                Location = loc,
                Size     = size,
                Font     = bold
            };

            // Панель предпросмотра создаём заранее: на неё ссылаются обработчики
            // всех остальных полей, а out-параметр в лямбду захватить нельзя.
            var prv = new Panel
            {
                BackColor   = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            int inner = grp.ClientSize.Width;
            int listX = S(12);

            // Правая колонка: ширину подписей берём по самой длинной из них,
            // и уже под неё подгоняем список — так «Масштаб:» не наезжает на
            // поле ввода ни при каком DPI.
            int labW = 0;
            foreach (var t in new[] { "Цвет:", "Угол:", "Масштаб:" })
                labW = Math.Max(labW, TextSize(t, body).Width);
            labW += S(8);

            int fieldW   = Math.Max(S(80), TextSize("-360.0", body).Width + S(34)); // + стрелки
            int rightMin = labW + fieldW + TextSize("°", body).Width + S(24);

            int listW = Math.Max(S(110),
                Math.Min(inner * 40 / 100, inner - listX - rightMin - S(26)));

            // Заголовок рамки съедает верх — подписи начинаются под ним, иначе
            // «Образец:» налезает на «Штриховка выемки».
            var lblPattern = Lbl("Образец:", body, listX, HeaderHeight(bold));
            grp.Controls.Add(lblPattern);

            int listY = lblPattern.Bottom + S(4);
            int listH = size.Height - listY - S(16);

            var lstLocal = new ListBox
            {
                Location       = new Point(listX, listY),
                Size           = new Size(listW, listH),
                Font           = new Font("Consolas", 8.5f),
                IntegralHeight = false
            };
            foreach (var n in HatchPatternCatalog.Names) lstLocal.Items.Add(n);
            SelectByText(lstLocal, style.Pattern);
            lstLocal.SelectedIndexChanged += (s, e) =>
            {
                if (lstLocal.SelectedItem != null)
                {
                    style.Pattern = lstLocal.SelectedItem.ToString()!;
                    prv.Invalidate();
                }
            };
            grp.Controls.Add(lstLocal);

            int rx = listX + listW + S(14);
            int rw = inner - rx - S(12);
            int ry = listY;

            // Поле ввода ужимаем под фактический остаток колонки — иначе при
            // крупном шрифте стрелки счётчика уезжают за рамку группы.
            int degW = TextSize("°", body).Width + S(6);
            fieldW = Math.Min(fieldW, Math.Max(S(46), rw - labW - degW - S(4)));

            int rowGap = S(8);

            // Цвет
            var lblColor = Lbl("Цвет:", body, rx, ry);
            var pnlColorLocal = ColorPanel(style.ColorAci, new Point(rx + labW, ry),
                                           S(46), Math.Max(S(24), lblColor.Height + S(4)));
            pnlColorLocal.Click += (s, e) =>
            {
                if (PickAci(style.ColorAci, out int aci))
                {
                    style.ColorAci = aci;
                    pnlColorLocal.BackColor = AciColorPickerForm.AciToColor(aci);
                    prv.Invalidate();
                }
            };
            grp.Controls.Add(lblColor);
            grp.Controls.Add(pnlColorLocal);
            PlaceRow(ry, pnlColorLocal.Height, lblColor, pnlColorLocal);
            ry += pnlColorLocal.Height + rowGap;

            // Угол
            var lblAngle = Lbl("Угол:", body, rx, ry);
            var nudAngleLocal = new NumericUpDown
            {
                Minimum = -360m, Maximum = 360m, DecimalPlaces = 1, Increment = 15m,
                Value = (decimal)Math.Max(-360, Math.Min(360, style.Angle)),
                Location = new Point(rx + labW, ry), Width = fieldW, Font = body
            };
            nudAngleLocal.ValueChanged += (s, e)
                => { style.Angle = (double)nudAngleLocal.Value; prv.Invalidate(); };
            var lblDeg = Lbl("°", body, rx + labW + fieldW + S(4), ry);
            // Не влез в колонку — лучше убрать, чем показывать обрезанным.
            lblDeg.Visible = lblDeg.Left + TextSize("°", body).Width <= rx + rw;
            grp.Controls.Add(lblAngle);
            grp.Controls.Add(nudAngleLocal);
            grp.Controls.Add(lblDeg);
            PlaceRow(ry, nudAngleLocal.Height, lblAngle, nudAngleLocal, lblDeg);
            ry += nudAngleLocal.Height + rowGap;

            // Масштаб
            var lblScale = Lbl("Масштаб:", body, rx, ry);
            var nudScaleLocal = new NumericUpDown
            {
                Minimum = 0.01m, Maximum = 10000m, DecimalPlaces = 2, Increment = 0.01m,
                Value = (decimal)Math.Max(0.01, Math.Min(10000, style.Scale)),
                Location = new Point(rx + labW, ry), Width = fieldW, Font = body
            };
            nudScaleLocal.ValueChanged += (s, e)
                => { style.Scale = (double)nudScaleLocal.Value; prv.Invalidate(); };
            grp.Controls.Add(lblScale);
            grp.Controls.Add(nudScaleLocal);
            PlaceRow(ry, nudScaleLocal.Height, lblScale, nudScaleLocal);
            ry += nudScaleLocal.Height + S(12);

            // Предпросмотр — занимает всю оставшуюся высоту.
            // Подпись длинная: если в колонку не влезает, берём вариант короче,
            // чтобы текст не обрезало и не наезжало на окошко.
            string prvText = $"Предпросмотр (сетка {_cellSize:0.###} м):";
            if (TextSize(prvText, body).Width > rw)
                prvText = $"Сетка {_cellSize:0.###} м:";
            var lblPrv = Lbl(prvText, body, rx, ry);
            if (lblPrv.Width > rw)   // и короткий вариант не влез — обрежем многоточием
            {
                lblPrv.Width        = rw;
                lblPrv.AutoEllipsis = true;
            }
            new ToolTip().SetToolTip(lblPrv,
                "Окошко показывает одну ячейку сетки в натуральную густоту.\n" +
                "Если здесь сплошная заливка — такой же она будет и на чертеже:\n" +
                "увеличьте масштаб образца.");
            grp.Controls.Add(lblPrv);
            ry = lblPrv.Bottom + S(3);

            int prvH = Math.Max(S(60), listY + listH - ry);
            prv.Location = new Point(rx, ry);
            prv.Size     = new Size(rw, prvH);
            prv.Paint += (s, e) =>
            {
                var box = new Rectangle(0, 0, prv.ClientSize.Width, prv.ClientSize.Height);
                e.Graphics.Clear(Color.White);
                HatchPatternCatalog.Paint(e.Graphics, box, style.Pattern,
                    style.Angle, style.Scale, AciColorPickerForm.AciToColor(style.ColorAci),
                    box.Width / _cellSize);
            };
            grp.Controls.Add(prv);

            lst      = lstLocal;
            pnlColor = pnlColorLocal;
            nudAngle = nudAngleLocal;
            nudScale = nudScaleLocal;
            preview  = prv;
            return grp;
        }

        // ── Предпросмотр линии нулевых работ ──────────────────────────────────

        private void PaintZeroPreview(object? sender, PaintEventArgs e)
        {
            var p = (Panel)sender!;
            var g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var color = AciColorPickerForm.AciToColor(ZeroStyle.ColorAci);

            // Вес «по слою» показываем тонкой линией — как AutoCAD по умолчанию.
            float widthPx = ZeroStyle.LineWeight < 0
                ? 1f
                : Math.Max(1f, (float)(ZeroStyle.LineWeight * 3.0));

            using var pen = new Pen(color, widthPx);

            var dashes = FindDashes(ZeroStyle.LineType);
            if (dashes.Length > 0)
            {
                var pat = new List<float>();
                foreach (var d in dashes)
                {
                    double len = Math.Abs(d) * 8.0;
                    if (len < 0.5) len = 0.5;
                    pat.Add((float)len);
                }
                if (pat.Count % 2 != 0) pat.Add(pat[pat.Count - 1]);
                try
                {
                    pen.DashStyle   = DashStyle.Custom;
                    pen.DashPattern = pat.ToArray();
                }
                catch { pen.DashStyle = DashStyle.Solid; }
            }

            int y = p.ClientSize.Height / 2;
            g.DrawLine(pen, 6, y, p.ClientSize.Width - 6, y);
        }

        private double[] FindDashes(string name)
        {
            foreach (var lt in _linetypes)
                if (string.Equals(lt.Name, name, StringComparison.OrdinalIgnoreCase))
                    return lt.Dashes;
            return Array.Empty<double>();
        }

        // ── Мелкие помощники ──────────────────────────────────────────────────

        private static Panel ColorPanel(int aci, Point loc, int w, int h) => new Panel
        {
            Location    = loc,
            Size        = new Size(w, h),
            BackColor   = AciColorPickerForm.AciToColor(aci),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor      = Cursors.Hand
        };

        private static Panel PreviewPanel(Point loc, Size size) => new Panel
        {
            Location    = loc,
            Size        = size,
            BackColor   = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        private bool PickAci(int current, out int aci)
        {
            using var dlg = new AciColorPickerForm(current);
            if (dlg.ShowDialog(this) == DialogResult.OK) { aci = dlg.SelectedAci; return true; }
            aci = current;
            return false;
        }

        private static void SelectByText(ListBox lst, string text)
        {
            int i = lst.FindStringExact(text);
            lst.SelectedIndex = i >= 0 ? i : (lst.Items.Count > 0 ? 0 : -1);
        }

        private static void SelectByText(ComboBox cmb, string text)
        {
            int i = cmb.FindStringExact(text);
            cmb.SelectedIndex = i >= 0 ? i : (cmb.Items.Count > 0 ? 0 : -1);
        }
    }
}
