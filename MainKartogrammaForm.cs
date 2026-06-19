using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AcadObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace KartogrammaPlugin
{
    /// <summary>Тип запроса на выбор точек в чертеже (pick)</summary>
    public enum PickRequest { None, BasePoint, Angle, OuterBoundary, InnerBoundaries, CopyKartogramma, SizeX, SizeY, CalloutLabel }

    /// <summary>Главный диалог «Картограмма земляных работ» — плоский двухколоночный макет</summary>
    public sealed class MainKartogrammaForm : Form
    {
        // ── Поверхности ──────────────────────────────────────────────────────────
        private ComboBox      cmbSurf1     = null!;
        private ComboBox      cmbSurf2     = null!;

        // ── Сетка ───────────────────────────────────────────────────────────────
        private Label         lblBase      = null!;
        private CheckBox      chkAutoBase  = null!;
        private NumericUpDown nudSizeX     = null!;
        private NumericUpDown nudSizeY     = null!;
        private CheckBox      chkDontClip   = null!;
        private CheckBox      chkAutoBounds = null!;
        private Button        btnOuterBound = null!;
        private Button        btnInnerBound = null!;
        private Button        btnClearOuter = null!;
        private Button        btnClearInner = null!;
        private Label         lblOuterBound = null!;
        private Label         lblInnerBound = null!;
        private AcadObjectId  _outerBoundaryId = AcadObjectId.Null;
        private List<AcadObjectId> _innerBoundaryIds = new();
        private NumericUpDown nudAngle     = null!;
        private CheckBox      chkHorizontal = null!;
        private Button        btnAng        = null!;
        private bool          _basePicked;
        private double        _baseX, _baseY;

        // ── Сессия (статика — живёт всё время работы AutoCAD) ───────────────────
        private static string?  _sSurf1, _sSurf2, _sStyle, _sVolStyle, _sTblStyle;
        private static int      _sSurfIdx1 = 0, _sSurfIdx2 = 1;
        private static decimal  _sSizeX    = 1m,    _sSizeY      = 1m;
        private static decimal  _sAngle    = 0m;
        private static int      _sAngType  = 0;       // 0=Свой, 1=Горизонтально
        private static decimal  _sHeight   = 0.15m,  _sVolHeight = 0.25m;
        private static int      _sPrecIdx  = 2,       _sVolPrecIdx = 2;
        private static int      _sDecIdx   = 0;
        private static bool     _sDontClip = false,  _sHide = true;
        private static bool     _sHideVol  = true;
        private static bool     _sHideTable = false;
        private static bool     _sMinVolChk = true;
        private static decimal  _sMinVol   = 0.01m,  _sNodeStep  = 0.05m;
        private static int      _sVolMethodIdx = 0;  // 0=Триангуляция, 1=Квадраты
        private static int      _sColDesign = 1,     _sColExist  = 5;
        private static int      _sColWork  = 3,      _sColVolume = 7;
        private static int      _sColTable = 7,      _sTablePos  = 0;
        private static decimal  _sTblHeight = 0.25m;
        private static bool     _sBasePicked;
        private static double   _sSavedBaseX, _sSavedBaseY;
        private static bool     _sAutoBounds = true;
        private static AcadObjectId _sOuterBoundaryId = AcadObjectId.Null;
        private static List<AcadObjectId> _sInnerBoundaryIds = new();
        private static bool     _sessionSaved;

        // ── Снапшоты последней успешной отрисовки (для кнопки «Перерисовать»):
        //    структурный — изменение требует полной перестройки;
        //    косметический — можно обновить in-place (цвета, высоты, стили).
        private static string?  _sLastStructSnapshot;
        private static string?  _sLastCosmeticSnapshot;
        private static bool     _sLastHadVolume;

        // ── Имена слоёв (настраиваются через диалог-шестерёнку) ─────────────────
        private static string _sLayGrid   = "Картограмма сетка";
        private static string _sLayText   = "Картограмма текст";
        private static string _sLayWork   = "Картограмма разница";
        private static string _sLayExist  = "Картограмма черная";
        private static string _sLayDesign = "Картограмма красная";
        private static string _sLayVolume = "Картограмма объём";
        private static string _sLayTable  = "Картограмма таблица";

        /// <summary>
        /// Текущие имена всех слоёв картограммы (с учётом пользовательских
        /// переименований через диалог-шестерёнку). Используется при
        /// «Копировать картограмму», чтобы собрать ВСЕ объекты, созданные
        /// плагином, независимо от того, как их назвал пользователь.
        /// </summary>
        public static IEnumerable<string> CurrentLayerNames => new[]
        {
            _sLayGrid, _sLayText, _sLayWork,
            _sLayExist, _sLayDesign, _sLayVolume, _sLayTable
        };

        // ── Отметки ─────────────────────────────────────────────────────────────
        private ComboBox      cmbStyle     = null!;
        private ComboBox      cmbPrecision = null!;
        private NumericUpDown nudHeight    = null!;
        private ComboBox      cmbDecimal   = null!;
        private CheckBox      chkHide      = null!;

        // ── Цвета ───────────────────────────────────────────────────────────────
        private Panel         pnlExisting  = null!;
        private Panel         pnlDesign    = null!;
        private Panel         pnlWork      = null!;
        private Panel         pnlVolume    = null!;

        // ── Объёмы ──────────────────────────────────────────────────────────────
        private CheckBox      chkHideVol   = null!;
        private CheckBox      chkHideTable = null!;
        private ComboBox      cmbStyleVol  = null!;
        private ComboBox      cmbVolPrec   = null!;
        private NumericUpDown nudVolHeight = null!;
        private CheckBox      chkMinVol    = null!;
        private NumericUpDown nudMinVol    = null!;
        private NumericUpDown nudNodeStep  = null!;
        private ComboBox      cmbVolMethod = null!;
        private Panel         pnlTable     = null!;
        private ComboBox      cmbTablePos  = null!;
        private ComboBox      cmbStyleTable = null!;

        private Label         lblStatus    = null!;
        private ProgressBar   prgBar       = null!;
        private NumericUpDown nudTblHeight = null!;

        private readonly Document       _doc;
        private readonly IList<string>  _surfs;
        private readonly IList<string>  _styles;
        private readonly KartogrammaOptions _opts = new();

        /// <summary>Какой pick запрашивает форма (None = не нужен)</summary>
        public PickRequest PendingPick { get; private set; } = PickRequest.None;

        public MainKartogrammaForm(Document doc, IList<string> surfs,
            IList<string> styles, string docName)
        {
            _doc    = doc;
            _surfs  = surfs;
            _styles = styles;
            EnsurePersistedLoaded();   // подтянуть сохранённые на диске настройки (1 раз за сессию)
            BuildUI(docName);
            if (_sessionSaved) RestoreSession();
            FormClosing += (s, e) => SaveSession();
        }

        // ════════════════════════════════════════════════════════════════════════
        //  DPI-масштабирование — все пиксельные размеры умножаются на _dpiScale
        // ════════════════════════════════════════════════════════════════════════
        private float _dpiScale = 1f;
        private int   S(int v) => (int)(v * _dpiScale);

        // ════════════════════════════════════════════════════════════════════════
        //  Построение интерфейса — плоский двухколоночный макет (без вкладок)
        // ════════════════════════════════════════════════════════════════════════
        private void BuildUI(string docName)
        {
            Text            = "Картограмма земляных работ";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            AutoScaleMode   = AutoScaleMode.None;  // масштабируем вручную

            // Определяем DPI-масштаб
            using (var g = CreateGraphics()) { _dpiScale = g.DpiX / 96f; }
            Font = new Font("Segoe UI", 8.5f);

            // Колонки
            int lx = S(6),   lw = S(366);
            int rx = S(378), rw = S(374);

            int y = S(8);

            // Заголовок документа
            Controls.Add(new Label
            {
                Text      = docName,
                Location  = new Point(S(8), y),
                Size      = new Size(S(744), S(18)),
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            });
            y += S(26);

            int leftY  = y;
            int rightY = y;

            // ════════════════════════════════════════════════════════════════
            //  ЛЕВАЯ КОЛОНКА
            // ════════════════════════════════════════════════════════════════

            // ── Поверхности ────────────────────────────────────────────────
            var grpSurf = Grp("Поверхности", lx, leftY, lw, S(82));
            grpSurf.Controls.Add(Lbl("Чёрная (было):", S(10), S(26)));
            cmbSurf1 = Cmb(_surfs, 0, new Point(S(116), S(23)), new Size(S(240), S(22)));
            grpSurf.Controls.Add(cmbSurf1);

            grpSurf.Controls.Add(Lbl("Красная (стало):", S(10), S(54)));
            cmbSurf2 = Cmb(_surfs, 1, new Point(S(116), S(51)), new Size(S(240), S(22)));
            grpSurf.Controls.Add(cmbSurf2);

            Controls.Add(grpSurf);
            leftY += S(88);

            // ── Сетка квадратов ────────────────────────────────────────────
            var grpGrid = Grp("Сетка квадратов", lx, leftY, lw, S(92));

            // Строка 1: «Базовая точка:» [✓ Авто] координаты [>>]
            grpGrid.Controls.Add(Lbl("Базовая точка:", S(10), S(20)));
            chkAutoBase = new CheckBox
            {
                Text     = "Авто",
                Checked  = true,
                Location = new Point(S(104), S(18)),
                Size     = new Size(S(54), S(20)),
                Font     = new Font("Segoe UI", 7.5f)
            };
            chkAutoBase.CheckedChanged += OnAutoBaseChanged;
            grpGrid.Controls.Add(chkAutoBase);
            lblBase = new Label
            {
                Text      = "(авто)",
                Location  = new Point(S(160), S(20)),
                Size      = new Size(S(160), S(16)),
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = Color.Gray
            };
            grpGrid.Controls.Add(lblBase);
            var btnPick = new Button { Text = ">>", Location = new Point(S(324), S(17)), Size = new Size(S(32), S(22)) };
            new ToolTip().SetToolTip(btnPick, "Указать базовую точку сетки на чертеже — нижний левый угол");
            btnPick.Click += OnPickBase;
            grpGrid.Controls.Add(btnPick);

            // Строка 2: По горизонт., м:  [nud] [>>]
            grpGrid.Controls.Add(Lbl("По горизонт., м:", S(10), S(42)));
            nudSizeX = Nud(0.01m, 200m, 1m, 2, 0.5m);
            nudSizeX.Location = new Point(S(130), S(40)); nudSizeX.Size = new Size(S(70), S(22));
            grpGrid.Controls.Add(nudSizeX);
            var btnPickSizeX = new Button { Text = ">>", Location = new Point(S(324), S(40)), Size = new Size(S(32), S(22)) };
            new ToolTip().SetToolTip(btnPickSizeX, "Указать размер по горизонтали на чертеже (две точки)");
            btnPickSizeX.Click += OnPickSizeX;
            grpGrid.Controls.Add(btnPickSizeX);

            // Строка 3: По вертикали, м:  [nud] [>>]
            grpGrid.Controls.Add(Lbl("По вертикали, м:", S(10), S(66)));
            nudSizeY = Nud(0.01m, 200m, 1m, 2, 0.5m);
            nudSizeY.Location = new Point(S(130), S(64)); nudSizeY.Size = new Size(S(70), S(22));
            grpGrid.Controls.Add(nudSizeY);
            var btnPickSizeY = new Button { Text = ">>", Location = new Point(S(324), S(64)), Size = new Size(S(32), S(22)) };
            new ToolTip().SetToolTip(btnPickSizeY, "Указать размер по вертикали на чертеже (две точки)");
            btnPickSizeY.Click += OnPickSizeY;
            grpGrid.Controls.Add(btnPickSizeY);
            Controls.Add(grpGrid);
            int gridTopL   = leftY;          // верхняя грань Сетки квадратов
            int gridBottom = leftY + S(92); // нижняя грань Сетки квадратов
            leftY += S(98);

            // ── Угол поворота ──────────────────────────────────────────────
            int angTop = leftY; // верхняя грань Угла поворота
            var grpAng = Grp("Угол поворота картограммы", lx, leftY, lw, S(56));
            chkHorizontal = new CheckBox
            {
                Text     = "Горизонтально",
                Checked  = true,
                Location = new Point(S(10), S(24)),
                Size     = new Size(S(116), S(20)),
                Font     = new Font("Segoe UI", 7.5f)
            };
            chkHorizontal.CheckedChanged += OnAngTypeChanged;
            grpAng.Controls.Add(chkHorizontal);

            nudAngle = Nud(-360m, 360m, 0m, 4, 0.1m);
            nudAngle.Location = new Point(S(130), S(22)); nudAngle.Size = new Size(S(80), S(22));
            // По умолчанию «Горизонтально» включено → счётчик неактивен и обнулён.
            // Активируется только когда пользователь снимет чекбокс или задаст
            // угол по объекту через кнопку «>>».
            nudAngle.Enabled = false;
            grpAng.Controls.Add(nudAngle);
            grpAng.Controls.Add(new Label { Text = "°", Location = new Point(S(214), S(26)), AutoSize = true });

            // Кнопка «>>» всегда активна — позволяет задать угол по объекту
            // даже когда «Горизонтально» включено: после выбора объекта чекбокс
            // автоматически снимется и счётчик получит выбранное значение.
            btnAng = new Button { Text = ">>", Location = new Point(S(324), S(23)), Size = new Size(S(32), S(22)) };
            new ToolTip().SetToolTip(btnAng, "Задать угол поворота по двум точкам на чертеже");
            btnAng.Click += OnPickAngle;
            grpAng.Controls.Add(btnAng);
            Controls.Add(grpAng);
            leftY += S(62);

            // ── Границы ────────────────────────────────────────────────────
            var grpBnd = Grp("Границы", lx, leftY, lw, S(78));
            // Левая колонка — чекбоксы
            chkAutoBounds = new CheckBox
            {
                Text     = "Границы автоматически",
                Checked  = true,
                Location = new Point(S(10), S(24)),
                Size     = new Size(S(170), S(20)),
                Font     = new Font("Segoe UI", 7.5f)
            };
            chkAutoBounds.CheckedChanged += OnAutoBoundsChanged;
            grpBnd.Controls.Add(chkAutoBounds);
            chkDontClip = new CheckBox
            {
                Text     = "Не обрезать сетку квадратов",
                Location = new Point(S(10), S(50)),
                Size     = new Size(S(170), S(20)),
                Font     = new Font("Segoe UI", 7.5f)
            };
            grpBnd.Controls.Add(chkDontClip);
            // Правая колонка — кнопки выбора границ
            lblOuterBound = new Label
            {
                Text      = "Наружная",
                Location  = new Point(S(190), S(24)),
                Size      = new Size(S(95), S(16)),
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = SystemColors.ControlText
            };
            grpBnd.Controls.Add(lblOuterBound);
            btnClearOuter = new Button { Text = "✕", Location = new Point(S(288), S(21)), Size = new Size(S(30), S(22)), Enabled = false };
            new ToolTip().SetToolTip(btnClearOuter, "Сбросить наружную границу");
            btnClearOuter.Click += OnClearOuterBound;
            grpBnd.Controls.Add(btnClearOuter);
            btnOuterBound = new Button { Text = ">>", Location = new Point(S(324), S(21)), Size = new Size(S(32), S(22)), Enabled = false };
            new ToolTip().SetToolTip(btnOuterBound, "Выбрать замкнутую полилинию наружной границы на чертеже");
            btnOuterBound.Click += OnPickOuterBound;
            grpBnd.Controls.Add(btnOuterBound);
            lblInnerBound = new Label
            {
                Text      = "Внутренние",
                Location  = new Point(S(190), S(50)),
                Size      = new Size(S(95), S(16)),
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = SystemColors.ControlText
            };
            grpBnd.Controls.Add(lblInnerBound);
            btnClearInner = new Button { Text = "✕", Location = new Point(S(288), S(47)), Size = new Size(S(30), S(22)), Enabled = false };
            new ToolTip().SetToolTip(btnClearInner, "Сбросить внутренние границы");
            btnClearInner.Click += OnClearInnerBound;
            grpBnd.Controls.Add(btnClearInner);
            btnInnerBound = new Button { Text = ">>", Location = new Point(S(324), S(47)), Size = new Size(S(32), S(22)), Enabled = false };
            new ToolTip().SetToolTip(btnInnerBound, "Выбрать замкнутые полилинии внутренних границ (дырок) на чертеже");
            btnInnerBound.Click += OnPickInnerBounds;
            grpBnd.Controls.Add(btnInnerBound);
            Controls.Add(grpBnd);
            leftY += S(84);

            // ════════════════════════════════════════════════════════════════
            //  КНОПКИ ДЕЙСТВИЙ — 2 колонки × 4 ряда под блоком «Границы».
            // ════════════════════════════════════════════════════════════════
            int actW = S(178), actH = S(28), actGapY = S(4);
            int colGap = S(6);
            int col1X = lx + S(1);
            int col2X = col1X + actW + colGap;
            int rY0   = leftY + S(6);
            int rY1   = rY0 + actH + actGapY;
            int rY2   = rY1 + actH + actGapY;
            int rY3   = rY2 + actH + actGapY;

            // Колонка 1
            AddActBtn("Создать сетку",   col1X, rY0, actW, actH, OnBuildGrid,
                "Построить сетку квадратов по выбранным поверхностям");
            AddActBtn("Удалить сетку",   col1X, rY1, actW, actH, OnDeleteGrid,
                "Удалить все линии сетки из чертежа");
            AddActBtn("Выноска отметок", col1X, rY2, actW, actH, OnPickCalloutLabel,
                "Перенести тройку отметок в читаемое место");

            // Колонка 1, ряд 4: [Слои по размеру]  +  «Версия: 1.0.0» ниже
            AddActBtn("Слои", col1X, rY3, actW, actH, OnLayerSettings,
                "Настройка имён слоёв картограммы");

            // Колонка 2
            AddActBtn("Рассчитать объём",       col2X, rY0, actW, actH, OnCalculateVolume,
                "Подписать отметки, рассчитать объёмы и построить итоговую таблицу");
            AddActBtn("Удалить объём",          col2X, rY1, actW, actH, OnDeleteVolume,
                "Удалить подписи отметок, объёмов и итоговую таблицу");
            AddActBtn("Перерисовать",           col2X, rY2, actW, actH, OnRedraw,
                "Обновить цвета, высоты и стили текста без полной перестройки");
            AddActBtn("Копировать картограмму", col2X, rY3, actW, actH, OnCopyKartogramma,
                "Выделить все объекты картограммы и скопировать в буфер обмена с базовой точкой (Ctrl+Shift+C)");

            leftY = rY3 + actH + S(6);

            // ════════════════════════════════════════════════════════════════
            //  ПРАВАЯ КОЛОНКА
            // ════════════════════════════════════════════════════════════════

            // ── Отметки ────────────────────────────────────────────────────
            // Низ Отметок = верх Сетки квадратов (gridTop = y + S(82))
            int gridTop = rightY + S(82); // rightY здесь = y
            int grpLabelsH = gridTop - rightY;
            var grpL = Grp("Отметки", rx, rightY, rw, grpLabelsH);

            grpL.Controls.Add(Lbl("Стиль текста:", S(10), S(20)));
            cmbStyle = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(92), S(17)),
                Size          = new Size(S(130), S(22))
            };
            foreach (var s in _styles) cmbStyle.Items.Add(s);
            if (cmbStyle.Items.Count > 0) cmbStyle.SelectedIndex = 0;
            grpL.Controls.Add(cmbStyle);

            grpL.Controls.Add(new Label
            {
                Text      = "Высота:",
                Location  = new Point(S(228), S(20)),
                Size      = new Size(S(48), S(16)),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft
            });
            nudHeight = Nud(0.001m, 25m, 0.15m, 3, 0.05m);
            nudHeight.Location = new Point(S(280), S(17)); nudHeight.Size = new Size(S(60), S(22));
            grpL.Controls.Add(nudHeight);
            grpL.Controls.Add(new Label { Text = "м", Location = new Point(S(344), S(20)), AutoSize = true });

            // Ряд 2: Точность + Разделитель
            grpL.Controls.Add(Lbl("Точность:", S(10), S(44)));
            cmbPrecision = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(72), S(41)),
                Size          = new Size(S(62), S(22))
            };
            cmbPrecision.Items.AddRange(new object[] { "0", "0.0", "0.00", "0.000" });
            cmbPrecision.SelectedIndex = 2;
            grpL.Controls.Add(cmbPrecision);

            grpL.Controls.Add(Lbl("Разделитель:", S(140), S(44)));
            cmbDecimal = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(218), S(41)),
                Size          = new Size(S(80), S(22))
            };
            cmbDecimal.Items.AddRange(new object[] { "Точка (.)", "Запятая (,)" });
            cmbDecimal.SelectedIndex = 0;
            grpL.Controls.Add(cmbDecimal);

            chkHide = new CheckBox
            {
                Text     = "Скрывать задний план",
                Checked  = true,
                Location = new Point(S(10), S(62)),
                Size     = new Size(S(160), S(18))
            };
            grpL.Controls.Add(chkHide);

            Controls.Add(grpL);

            // ── Цвет ───────────────────────────────────────────────────────
            // Верх и низ Цвета = верх и низ Сетки квадратов
            int grpColorH = gridBottom - gridTopL; // S(92), как у Сетки
            rightY = gridTopL;
            var grpC = Grp("Цвет", rx, rightY, rw, grpColorH);
            int colorX = (rw - S(250)) / 2; // центрируем 4 пары в рамке
            AddColorRow2(grpC, "Красная",          ref pnlDesign!,   1, "Разница отметок", ref pnlWork!,    3, colorX, S(22));
            AddColorRow2(grpC, "Чёрная",           ref pnlExisting!, 5, "Объём",           ref pnlVolume!,  7, colorX, S(52));
            Controls.Add(grpC);

            // ── Объёмы ─────────────────────────────────────────────────────
            // Верх Объёмов = верх Угла поворота (angTop)
            rightY = angTop;
            int volH = (rY3 + actH) - rightY; // нижняя кромка = нижняя кромка кнопок
            var grpVol = Grp("Объёмы", rx, rightY, rw, volH);

            grpVol.Controls.Add(Lbl("Стиль текста:", S(10), S(26)));
            cmbStyleVol = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(96), S(23)),
                Size          = new Size(S(130), S(22))
            };
            foreach (var s in _styles) cmbStyleVol.Items.Add(s);
            if (cmbStyleVol.Items.Count > 0) cmbStyleVol.SelectedIndex = 0;
            grpVol.Controls.Add(cmbStyleVol);

            grpVol.Controls.Add(new Label
            {
                Text      = "Высота:",
                Location  = new Point(S(228), S(26)),
                Size      = new Size(S(48), S(16)),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft
            });
            nudVolHeight = Nud(0.001m, 25m, 0.25m, 3, 0.05m);
            nudVolHeight.Location = new Point(S(280), S(23)); nudVolHeight.Size = new Size(S(60), S(22));
            grpVol.Controls.Add(nudVolHeight);
            grpVol.Controls.Add(new Label { Text = "м", Location = new Point(S(344), S(26)), AutoSize = true });

            grpVol.Controls.Add(Lbl("Точность:", S(10), S(54)));
            cmbVolPrec = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(96), S(51)),
                Size          = new Size(S(70), S(22))
            };
            cmbVolPrec.Items.AddRange(new object[] { "0", "0.0", "0.00", "0.000" });
            cmbVolPrec.SelectedIndex = 2;
            grpVol.Controls.Add(cmbVolPrec);

            chkHideVol = new CheckBox
            {
                Text     = "Скрывать задний план",
                Checked  = true,
                Location = new Point(S(170), S(52)),
                Size     = new Size(S(170), S(20)),
                Font     = new Font("Segoe UI", 7.5f)
            };
            grpVol.Controls.Add(chkHideVol);

            // ── Метод расчёта объёма ─────────────────────────────────────
            grpVol.Controls.Add(Lbl("Метод:", S(10), S(84)));
            cmbVolMethod = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(96), S(81)),
                Size          = new Size(S(244), S(22))
            };
            cmbVolMethod.Items.AddRange(new object[]
            {
                "Триангуляция (точно)",
                "Квадраты (по углам)"
            });
            cmbVolMethod.SelectedIndex = 0;
            cmbVolMethod.SelectedIndexChanged += (s, e) =>
                nudNodeStep.Enabled = cmbVolMethod.SelectedIndex == 0;
            grpVol.Controls.Add(cmbVolMethod);

            grpVol.Controls.Add(Lbl("Шаг субсетки:", S(10), S(112)));
            nudNodeStep = Nud(0.01m, 1.00m, 0.05m, 2, 0.01m);
            nudNodeStep.Location = new Point(S(96), S(109)); nudNodeStep.Size = new Size(S(60), S(22));
            grpVol.Controls.Add(nudNodeStep);
            grpVol.Controls.Add(new Label { Text = "м", Location = new Point(S(160), S(112)), AutoSize = true });
            grpVol.Controls.Add(new Label
            {
                Text      = "0.05 = 5 см  (СП 45.13330.201)",
                Location  = new Point(S(176), S(112)),
                AutoSize  = true,
                ForeColor = Color.Gray,
                Font      = new Font("Segoe UI", 7.5f)
            });

            grpVol.Controls.Add(Lbl("Мин. объём:", S(10), S(144)));
            nudMinVol = Nud(0m, 9999m, 0.01m, 3, 0.01m);
            nudMinVol.Location = new Point(S(96), S(141)); nudMinVol.Size = new Size(S(76), S(22));
            grpVol.Controls.Add(nudMinVol);
            grpVol.Controls.Add(new Label { Text = "м³", Location = new Point(S(176), S(144)), AutoSize = true });
            chkMinVol = new CheckBox
            {
                Text     = "",
                Checked  = true,
                Location = new Point(S(196), S(142)),
                Size     = new Size(S(20), S(20)),
                Font     = new Font("Segoe UI", 7.5f)
            };
            grpVol.Controls.Add(chkMinVol);

            // ── Итоговая таблица ──────────────────────────────────────────
            grpVol.Controls.Add(Lbl("Таблица, цвет:", S(10), S(176)));
            pnlTable          = ColorPanelNew(AciColorPickerForm.AciToColor(7));
            pnlTable.Location = new Point(S(96), S(172));
            pnlTable.Tag      = 7;
            pnlTable.Click   += OnColorClick;
            grpVol.Controls.Add(pnlTable);

            grpVol.Controls.Add(Lbl("Позиция:", S(136), S(176)));
            cmbTablePos = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(190), S(172)),
                Size          = new Size(S(172), S(22))
            };
            cmbTablePos.Items.AddRange(new object[] { "Сверху", "Снизу", "Слева", "Справа" });
            cmbTablePos.SelectedIndex = 0;
            grpVol.Controls.Add(cmbTablePos);

            grpVol.Controls.Add(Lbl("Шрифт табл.:", S(10), S(204)));
            nudTblHeight = Nud(0.001m, 25m, 0.25m, 3, 0.05m);
            nudTblHeight.Location = new Point(S(96), S(201)); nudTblHeight.Size = new Size(S(60), S(22));
            grpVol.Controls.Add(nudTblHeight);
            grpVol.Controls.Add(new Label { Text = "м", Location = new Point(S(160), S(204)), AutoSize = true });

            grpVol.Controls.Add(Lbl("Стиль табл.:", S(10), S(232)));
            cmbStyleTable = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(S(96), S(229)),
                Size          = new Size(S(130), S(22))
            };
            foreach (var s in _styles) cmbStyleTable.Items.Add(s);
            if (cmbStyleTable.Items.Count > 0) cmbStyleTable.SelectedIndex = 0;
            grpVol.Controls.Add(cmbStyleTable);

            chkHideTable = new CheckBox
            {
                Text     = "Скрыть задний план",
                Checked  = false,
                Location = new Point(S(232), S(203)),
                Size     = new Size(S(140), S(20)),
                Font     = new Font("Segoe UI", 7.5f)
            };
            grpVol.Controls.Add(chkHideTable);

            Controls.Add(grpVol);
            rightY += volH + S(6);

            // ════════════════════════════════════════════════════════════════
            //  НИЖНЯЯ ПАНЕЛЬ
            // ════════════════════════════════════════════════════════════════
            int botY = Math.Max(leftY, rightY) + S(6);

            lblStatus = new Label
            {
                Text      = "Готово к работе",
                Location  = new Point(S(8), botY),
                Size      = new Size(S(366), S(18)),
                ForeColor = Color.DarkGreen,
                Font      = new Font("Segoe UI", 7.5f)
            };
            Controls.Add(lblStatus);

            // Прогресс-бар по X-границам группы «Объёмы», на одной линии
            // со статусом. Окно закрывается крестиком в заголовке.
            prgBar = new ProgressBar
            {
                Location = new Point(rx, botY),
                Size     = new Size(rw, S(16)),
                Minimum  = 0,
                Maximum  = 100,
                Value    = 0,
                Style    = ProgressBarStyle.Continuous,
                Visible  = false
            };
            Controls.Add(prgBar);
            botY += S(22);

            ClientSize = new Size(S(760), botY + S(6));
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Обработчики кнопок
        // ════════════════════════════════════════════════════════════════════════

        private void OnPickBase(object? sender, EventArgs e)
        {
            // Закрываем диалог, запрашиваем pick в команде, потом диалог откроется снова
            PendingPick  = PickRequest.BasePoint;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        /// <summary>Установить базовую точку после pick (вызывается из команды)</summary>
        public void ApplyPickedBase(double x, double y)
        {
            _baseX = x; _baseY = y;
            _basePicked = true;
            _sBasePicked  = true;
            _sSavedBaseX  = x;
            _sSavedBaseY  = y;
            chkAutoBase.Checked = false;
            lblBase.Text      = $"X={x:F3}  Y={y:F3}";
            lblBase.ForeColor = SystemColors.ControlText;
        }

        /// <summary>Установить угол после pick (вызывается из команды)</summary>
        public void ApplyPickedAngle(double degrees)
        {
            // Снимаем «Горизонтально» (если стоит) — это активирует счётчик
            // через OnAngTypeChanged. Затем выставляем выбранное значение.
            if (chkHorizontal.Checked) chkHorizontal.Checked = false;
            nudAngle.Value = (decimal)Math.Max(-360, Math.Min(360, Math.Round(degrees, 4)));
        }

        private void OnAutoBaseChanged(object? sender, EventArgs e)
        {
            if (chkAutoBase.Checked)
            {
                _basePicked = false;
                lblBase.Text      = "(авто)";
                lblBase.ForeColor = Color.Gray;
            }
        }

        private void OnPickAngle(object? sender, EventArgs e)
        {
            PendingPick  = PickRequest.Angle;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        private void OnPickSizeX(object? sender, EventArgs e)
        {
            PendingPick  = PickRequest.SizeX;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        private void OnPickSizeY(object? sender, EventArgs e)
        {
            PendingPick  = PickRequest.SizeY;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        /// <summary>Установить размер ячейки по горизонтали после pick.</summary>
        public void ApplyPickedSizeX(double size)
        {
            nudSizeX.Value = (decimal)Math.Max((double)nudSizeX.Minimum,
                Math.Min((double)nudSizeX.Maximum, Math.Round(size, 2)));
        }

        /// <summary>Установить размер ячейки по вертикали после pick.</summary>
        public void ApplyPickedSizeY(double size)
        {
            nudSizeY.Value = (decimal)Math.Max((double)nudSizeY.Minimum,
                Math.Min((double)nudSizeY.Maximum, Math.Round(size, 2)));
        }

        private void OnBuildGrid(object? sender, EventArgs e)
        {
            if (!ValidateSurfaces()) return;
            SetStatus("Построение сетки…", Color.DarkBlue);
            try
            {
                var opts = CollectOptions();
                using (_doc.LockDocument())
                    new KartogrammaProcessor(_doc, opts).BuildGrid();
                _sLastStructSnapshot   = SnapshotStruct(opts);
                _sLastCosmeticSnapshot = SnapshotCosmetic(opts);
                _sLastHadVolume        = false;
                SetStatus("Сетка квадратов построена.", Color.DarkGreen);
            }
            catch (Exception ex) { SetStatus($"Ошибка: {ex.Message}", Color.Red); }
        }

        private void OnDeleteGrid(object? sender, EventArgs e)
        {
            SetStatus("Удаление сетки…", Color.DarkBlue);
            try
            {
                var opts = CollectOptions();
                using (_doc.LockDocument())
                    new KartogrammaProcessor(_doc, opts).DeleteGrid();
                SetStatus("Сетка удалена.", Color.DarkGreen);
            }
            catch (Exception ex) { SetStatus($"Ошибка: {ex.Message}", Color.Red); }
        }

        private void OnCalculateVolume(object? sender, EventArgs e)
        {
            if (!ValidateSurfaces()) return;
            SetStatus("Расчёт объёма…", Color.DarkBlue);
            prgBar.Value = 0;
            prgBar.Visible = true;
            try
            {
                var opts = CollectOptions();
                using (_doc.LockDocument())
                    new KartogrammaProcessor(_doc, opts, ReportProgress).CalculateVolume();
                _sLastStructSnapshot   = SnapshotStruct(opts);
                _sLastCosmeticSnapshot = SnapshotCosmetic(opts);
                _sLastHadVolume        = true;
                SetStatus("Объём рассчитан и подписан.", Color.DarkGreen);
            }
            catch (Exception ex) { SetStatus($"Ошибка: {ex.Message}", Color.Red); }
            finally { prgBar.Visible = false; }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Шестерёнка — диалог настройки имён слоёв
        // ════════════════════════════════════════════════════════════════════════
        private void OnLayerSettings(object? sender, EventArgs e)
        {
            using var dlg = new LayerSettingsForm(
                _sLayGrid, _sLayExist, _sLayDesign, _sLayWork, _sLayVolume, _sLayTable, _sLayText,
                ClientSize);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _sLayGrid   = dlg.GridLayer;
                _sLayExist  = dlg.ExistLayer;
                _sLayDesign = dlg.DesignLayer;
                _sLayWork   = dlg.WorkLayer;
                _sLayVolume = dlg.VolumeLayer;
                _sLayTable  = dlg.TableLayer;
                _sLayText   = dlg.TextLayer;
                SetStatus("Имена слоёв обновлены.", Color.DarkGreen);
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  «Копировать картограмму» — закрываем диалог, AppInit формирует
        //  selection set по слоям сетки/объёмов и запускает _.COPY
        // ════════════════════════════════════════════════════════════════════════
        private void OnPickCalloutLabel(object? sender, EventArgs e)
        {
            PendingPick  = PickRequest.CalloutLabel;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        private void OnCopyKartogramma(object? sender, EventArgs e)
        {
            PendingPick  = PickRequest.CopyKartogramma;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  «Перерисовать» — сравниваем со снапшотами:
        //   • структура изменилась → полная перестройка (+ пересчёт объёмов);
        //   • только косметика → in-place обновление цветов / высот / стилей;
        //   • ничего не изменилось → сообщение.
        // ════════════════════════════════════════════════════════════════════════
        private void OnRedraw(object? sender, EventArgs e)
        {
            if (_sLastStructSnapshot == null)
            {
                SetStatus("Нечего перерисовывать — сначала постройте картограмму.",
                    Color.DarkOrange);
                return;
            }
            if (!ValidateSurfaces()) return;

            var opts = CollectOptions();
            string nowStruct   = SnapshotStruct(opts);
            string nowCosmetic = SnapshotCosmetic(opts);

            bool structChanged   = nowStruct   != _sLastStructSnapshot;
            bool cosmeticChanged = nowCosmetic != _sLastCosmeticSnapshot;

            if (!structChanged && !cosmeticChanged)
            {
                SetStatus("Изменений нет — перерисовка не требуется.", Color.DarkOrange);
                return;
            }

            try
            {
                if (structChanged)
                {
                    SetStatus("Структурные изменения — полная перестройка…", Color.DarkBlue);
                    prgBar.Value = 0;
                    prgBar.Visible = _sLastHadVolume;
                    using (_doc.LockDocument())
                    {
                        var proc = new KartogrammaProcessor(_doc, opts, ReportProgress);
                        proc.DeleteVolume();
                        proc.DeleteGrid();
                        new KartogrammaProcessor(_doc, opts).BuildGrid();
                        if (_sLastHadVolume)
                            new KartogrammaProcessor(_doc, opts, ReportProgress).CalculateVolume();
                    }
                    SetStatus(_sLastHadVolume
                        ? "Картограмма перестроена (с пересчётом объёмов)."
                        : "Картограмма перестроена.", Color.DarkGreen);
                }
                else
                {
                    // Только косметика — обновляем в существующем
                    SetStatus("Обновление внешнего вида…", Color.DarkBlue);
                    int n;
                    using (_doc.LockDocument())
                        n = new KartogrammaProcessor(_doc, opts).UpdateAppearance();
                    SetStatus($"Обновлено объектов: {n}.", Color.DarkGreen);
                }
                _sLastStructSnapshot   = nowStruct;
                _sLastCosmeticSnapshot = nowCosmetic;
            }
            catch (Exception ex) { SetStatus($"Ошибка: {ex.Message}", Color.Red); }
            finally { prgBar.Visible = false; }
        }

        // ────────────────────────────────────────────────────────────────────────
        //  Снапшоты настроек. Структурные поля влияют на ГЕОМЕТРИЮ или СОДЕРЖИМОЕ
        //  подписей (числа); косметические — только на ЦВЕТ/ВЫСОТУ/СТИЛЬ текста.
        // ────────────────────────────────────────────────────────────────────────
        private static string SnapshotStruct(KartogrammaOptions o)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string D(double v) => v.ToString("R", inv);
            return string.Join("|", new[]
            {
                o.ExistingSurfaceName, o.DesignSurfaceName,
                D(o.CellSizeX), D(o.CellSizeY),
                o.AutoBasePoint.ToString(), D(o.BaseX), D(o.BaseY),
                D(o.RotationDegrees),
                o.AutoBounds.ToString(), o.OuterBoundaryId.ToString(),
                string.Join(",", o.InnerBoundaryIds),
                o.DontClipCells.ToString(),
                o.TextPrecision.ToString(), o.DecimalSeparator,
                o.VolumePrecision.ToString(),
                D(o.MinVolume), D(o.VolumeNodeStep),
                o.VolumeMethod.ToString(),
                o.TablePosition.ToString(),
                // hide-mask влияет на наличие фона текста — менять в существующих
                // объектах сложно, проще перестроить:
                o.HideMaskText.ToString(), o.HideMaskVolume.ToString(),
                o.HideMaskTable.ToString(),
            });
        }

        private static string SnapshotCosmetic(KartogrammaOptions o)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string D(double v) => v.ToString("R", inv);
            return string.Join("|", new[]
            {
                o.TextStyleName,
                D(o.LargeTextHeight), D(o.SmallTextHeight),
                o.ColorDesign.ToString(), o.ColorExisting.ToString(),
                o.ColorWork.ToString(),   o.ColorVolume.ToString(),
                o.ColorTable.ToString(),
                D(o.TableTextHeight), o.TableTextStyleName,
                D(o.VolumeTextHeight),
            });
        }

        private void OnDeleteVolume(object? sender, EventArgs e)
        {
            SetStatus("Удаление объёмов…", Color.DarkBlue);
            try
            {
                var opts = CollectOptions();
                using (_doc.LockDocument())
                    new KartogrammaProcessor(_doc, opts).DeleteVolume();
                SetStatus("Объёмы удалены.", Color.DarkGreen);
            }
            catch (Exception ex) { SetStatus($"Ошибка: {ex.Message}", Color.Red); }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Сбор параметров из UI
        // ════════════════════════════════════════════════════════════════════════
        /// <summary>Публичная обёртка для внешних вызовов (pick-команды).</summary>
        public KartogrammaOptions CollectOptionsPublic() => CollectOptions();

        private KartogrammaOptions CollectOptions()
        {
            var o = new KartogrammaOptions();

            // Поверхности
            o.ExistingSurfaceName = cmbSurf1.SelectedItem?.ToString() ?? "";
            o.DesignSurfaceName   = cmbSurf2.SelectedItem?.ToString() ?? "";

            // Сетка
            o.CellSizeX       = (double)nudSizeX.Value;
            o.CellSizeY       = (double)nudSizeY.Value;
            o.AutoBasePoint   = chkAutoBase.Checked;
            o.BaseX           = _baseX;
            o.BaseY           = _baseY;
            o.DontClipCells   = chkDontClip.Checked;
            o.AutoBounds      = chkAutoBounds.Checked;
            o.OuterBoundaryId = _outerBoundaryId;
            o.InnerBoundaryIds = new List<AcadObjectId>(_innerBoundaryIds);
            o.RotationDegrees = (double)nudAngle.Value;

            // Подписи
            o.TextStyleName    = cmbStyle.SelectedItem?.ToString() ?? "Standard";
            o.TextPrecision    = cmbPrecision.SelectedIndex;
            o.LargeTextHeight  = (double)nudHeight.Value;
            o.SmallTextHeight  = (double)nudHeight.Value;
            o.DecimalSeparator = cmbDecimal.SelectedIndex == 1 ? "," : ".";
            o.HideMaskText     = chkHide.Checked;
            o.HideMaskVolume   = chkHideVol.Checked;
            o.HideMaskTable    = chkHideTable.Checked;
            o.Annotative       = false;

            // Цвета
            o.ColorDesign   = (int)(pnlDesign.Tag   ?? 1);
            o.ColorExisting = (int)(pnlExisting.Tag ?? 5);
            o.ColorWork     = (int)(pnlWork.Tag     ?? 3);
            o.ColorVolume   = (int)(pnlVolume.Tag   ?? 7);
            o.ColorTable         = (int)(pnlTable.Tag ?? 7);
            o.TablePosition      = cmbTablePos.SelectedIndex;
            o.TableTextHeight    = (double)nudTblHeight.Value;
            o.TableTextStyleName = cmbStyleTable.SelectedItem?.ToString() ?? "Standard";

            // Объёмы
            o.VolumeTextHeight = (double)nudVolHeight.Value;
            o.VolumePrecision  = cmbVolPrec.SelectedIndex;
            o.MinVolume        = chkMinVol.Checked ? (double)nudMinVol.Value : 0.0;
            o.VolumeNodeStep   = (double)nudNodeStep.Value;
            o.VolumeMethod     = cmbVolMethod.SelectedIndex == 1
                ? VolumeMethod.Squares
                : VolumeMethod.Triangulation;
            o.DrawSummaryTable = true;

            // Слои (настраиваются через диалог-шестерёнку)
            o.GridLayerName   = _sLayGrid;
            o.TextLayerName   = _sLayText;
            o.WorkLayerName   = _sLayWork;
            o.ExistLayerName  = _sLayExist;
            o.DesignLayerName = _sLayDesign;
            o.VolumeLayerName = _sLayVolume;
            o.TableLayerName  = _sLayTable;

            return o;
        }

        private bool ValidateSurfaces()
        {
            if (cmbSurf1.SelectedItem == null || cmbSurf2.SelectedItem == null)
            {
                SetStatus("Выберите обе поверхности!", Color.Red);
                return false;
            }
            if (cmbSurf1.SelectedItem.ToString() == cmbSurf2.SelectedItem.ToString())
            {
                SetStatus("Чёрная и красная поверхности должны быть разными!", Color.Red);
                return false;
            }
            return true;
        }

        private void SetStatus(string msg, Color color)
        {
            lblStatus.Text      = msg;
            lblStatus.ForeColor = color;
            lblStatus.Refresh();
        }

        private void ReportProgress(string msg, int percent)
        {
            lblStatus.Text      = msg;
            lblStatus.ForeColor = Color.DarkBlue;
            lblStatus.Refresh();
            prgBar.Value = Math.Min(percent, 100);
            prgBar.Refresh();
            System.Windows.Forms.Application.DoEvents();
        }

        private void OnAngTypeChanged(object? sender, EventArgs e)
        {
            // «Горизонтально» включено → счётчик неактивен и обнулён.
            // Снято → пользователь может вводить угол вручную или через «>>».
            // Кнопка «>>» всегда активна, чтобы можно было задать угол по объекту
            // прямо из «горизонтального» режима — она же снимет чекбокс.
            bool custom = !chkHorizontal.Checked;
            nudAngle.Enabled = custom;
            if (!custom) nudAngle.Value = 0m; // Горизонтально = 0°
        }

        private void OnAutoBoundsChanged(object? sender, EventArgs e)
        {
            bool manual = !chkAutoBounds.Checked;
            btnOuterBound.Enabled = manual;
            btnInnerBound.Enabled = manual;
            btnClearOuter.Enabled = manual;
            btnClearInner.Enabled = manual;
            chkDontClip.Enabled   = manual;
            if (!manual) chkDontClip.Checked = false;
            // Надписи «Наружная» / «Внутренние» меняют цвет синхронно
            var lblColor = manual ? SystemColors.ControlText : SystemColors.GrayText;
            lblOuterBound.ForeColor = lblColor;
            lblInnerBound.ForeColor = lblColor;
        }

        private void OnPickOuterBound(object? sender, EventArgs e)
        {
            PendingPick  = PickRequest.OuterBoundary;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        private void OnClearOuterBound(object? sender, EventArgs e)
        {
            _outerBoundaryId        = AcadObjectId.Null;
            _sOuterBoundaryId       = AcadObjectId.Null;
            lblOuterBound.Text      = "Наружная";
            lblOuterBound.ForeColor = SystemColors.ControlText;
            SaveSession();
        }

        /// <summary>Установить наружную границу после pick</summary>
        public void ApplyPickedOuterBoundary(AcadObjectId id)
        {
            _outerBoundaryId   = id;
            _sOuterBoundaryId  = id;
            chkAutoBounds.Checked = false;
            lblOuterBound.Text      = "Наружная  \u2714";
            lblOuterBound.ForeColor = Color.DarkGreen;
        }

        private void OnPickInnerBounds(object? sender, EventArgs e)
        {
            PendingPick  = PickRequest.InnerBoundaries;
            SaveSession();
            DialogResult = DialogResult.Retry;
        }

        private void OnClearInnerBound(object? sender, EventArgs e)
        {
            _innerBoundaryIds       = new List<AcadObjectId>();
            _sInnerBoundaryIds      = new List<AcadObjectId>();
            lblInnerBound.Text      = "Внутренние";
            lblInnerBound.ForeColor = SystemColors.ControlText;
            SaveSession();
        }

        /// <summary>Установить внутренние границы после pick (замена всего списка).</summary>
        public void ApplyPickedInnerBoundaries(List<AcadObjectId> ids)
        {
            _innerBoundaryIds   = ids ?? new List<AcadObjectId>();
            _sInnerBoundaryIds  = new List<AcadObjectId>(_innerBoundaryIds);
            chkAutoBounds.Checked = false;
            if (_innerBoundaryIds.Count > 0)
            {
                lblInnerBound.Text      = $"Внутренние  \u2714 ({_innerBoundaryIds.Count})";
                lblInnerBound.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblInnerBound.Text      = "Внутренние";
                lblInnerBound.ForeColor = SystemColors.ControlText;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Сессия — сохранение/восстановление настроек между открытиями
        // ════════════════════════════════════════════════════════════════════════

        private void SaveSession()
        {
            _sSurf1      = cmbSurf1.SelectedItem?.ToString();
            _sSurf2      = cmbSurf2.SelectedItem?.ToString();
            _sSurfIdx1   = cmbSurf1.SelectedIndex;
            _sSurfIdx2   = cmbSurf2.SelectedIndex;
            _sSizeX      = nudSizeX.Value;
            _sSizeY      = nudSizeY.Value;
            _sDontClip   = chkDontClip.Checked;
            _sAutoBounds = chkAutoBounds.Checked;
            _sOuterBoundaryId = _outerBoundaryId;
            _sInnerBoundaryIds = new List<AcadObjectId>(_innerBoundaryIds);
            _sAngType    = chkHorizontal.Checked ? 1 : 0;
            _sAngle      = nudAngle.Value;
            _sStyle      = cmbStyle.SelectedItem?.ToString();
            _sPrecIdx    = cmbPrecision.SelectedIndex;
            _sHeight     = nudHeight.Value;
            _sDecIdx     = cmbDecimal.SelectedIndex;
            _sHide       = chkHide.Checked;
            _sColDesign  = (int)(pnlDesign.Tag   ?? 1);
            _sColExist   = (int)(pnlExisting.Tag ?? 5);
            _sColWork    = (int)(pnlWork.Tag     ?? 3);
            _sColVolume  = (int)(pnlVolume.Tag   ?? 7);
            _sColTable   = (int)(pnlTable.Tag    ?? 7);
            _sTablePos   = cmbTablePos.SelectedIndex;
            _sTblHeight  = nudTblHeight.Value;
            _sTblStyle   = cmbStyleTable.SelectedItem?.ToString();
            _sVolStyle   = cmbStyleVol.SelectedItem?.ToString();
            _sVolPrecIdx = cmbVolPrec.SelectedIndex;
            _sVolHeight  = nudVolHeight.Value;
            _sHideVol    = chkHideVol.Checked;
            _sHideTable  = chkHideTable.Checked;
            _sNodeStep   = nudNodeStep.Value;
            _sVolMethodIdx = cmbVolMethod.SelectedIndex;
            _sMinVolChk  = chkMinVol.Checked;
            _sMinVol     = nudMinVol.Value;
            _sBasePicked    = _basePicked;
            _sSavedBaseX    = _baseX;
            _sSavedBaseY    = _baseY;
            _sessionSaved   = true;

            // Сохраняем настройки-предпочтения на диск, чтобы они пережили
            // перезапуск AutoCAD (геометрия, привязанная к чертежу, не пишется).
            PersistSettings();
        }

        private void RestoreSession()
        {
            // Поверхности — восстанавливаем по имени (список мог измениться)
            SetComboByText(cmbSurf1, _sSurf1, _sSurfIdx1);
            SetComboByText(cmbSurf2, _sSurf2, _sSurfIdx2);

            nudSizeX.Value    = Clamp(_sSizeX,    nudSizeX.Minimum,    nudSizeX.Maximum);
            nudSizeY.Value    = Clamp(_sSizeY,    nudSizeY.Minimum,    nudSizeY.Maximum);
            chkDontClip.Checked = _sDontClip;
            chkAutoBounds.Checked = _sAutoBounds;
            _outerBoundaryId = _sOuterBoundaryId;
            if (!_outerBoundaryId.IsNull)
            {
                lblOuterBound.Text      = "Наружная  \u2714";
                lblOuterBound.ForeColor = Color.DarkGreen;
            }
            _innerBoundaryIds = new List<AcadObjectId>(_sInnerBoundaryIds);
            if (_innerBoundaryIds.Count > 0)
            {
                lblInnerBound.Text      = $"Внутренние  \u2714 ({_innerBoundaryIds.Count})";
                lblInnerBound.ForeColor = Color.DarkGreen;
            }
            OnAutoBoundsChanged(null, EventArgs.Empty);

            chkHorizontal.Checked = _sAngType == 1;
            nudAngle.Value        = Clamp(_sAngle, nudAngle.Minimum, nudAngle.Maximum);
            OnAngTypeChanged(null, EventArgs.Empty); // применить enabled

            SetComboByText(cmbStyle, _sStyle, -1);
            cmbPrecision.SelectedIndex = Math.Min(_sPrecIdx, cmbPrecision.Items.Count - 1);
            nudHeight.Value   = Clamp(_sHeight, nudHeight.Minimum, nudHeight.Maximum);
            cmbDecimal.SelectedIndex   = Math.Min(_sDecIdx, cmbDecimal.Items.Count - 1);
            chkHide.Checked   = _sHide;
            // chkAnnot removed from UI

            SetPanelColor(pnlDesign,   _sColDesign);
            SetPanelColor(pnlExisting, _sColExist);
            SetPanelColor(pnlWork,     _sColWork);
            SetPanelColor(pnlVolume,   _sColVolume);
            SetPanelColor(pnlTable,    _sColTable);
            cmbTablePos.SelectedIndex = Math.Min(_sTablePos, cmbTablePos.Items.Count - 1);
            nudTblHeight.Value = Clamp(_sTblHeight, nudTblHeight.Minimum, nudTblHeight.Maximum);
            SetComboByText(cmbStyleTable, _sTblStyle, -1);

            SetComboByText(cmbStyleVol, _sVolStyle, -1);
            cmbVolPrec.SelectedIndex = Math.Min(_sVolPrecIdx, cmbVolPrec.Items.Count - 1);
            nudVolHeight.Value = Clamp(_sVolHeight, nudVolHeight.Minimum, nudVolHeight.Maximum);
            chkHideVol.Checked = _sHideVol;
            chkHideTable.Checked = _sHideTable;
            nudNodeStep.Value  = Clamp(_sNodeStep, nudNodeStep.Minimum, nudNodeStep.Maximum);
            cmbVolMethod.SelectedIndex = Math.Min(Math.Max(_sVolMethodIdx, 0), cmbVolMethod.Items.Count - 1);
            nudNodeStep.Enabled = cmbVolMethod.SelectedIndex == 0;
            chkMinVol.Checked  = _sMinVolChk;
            nudMinVol.Value    = Clamp(_sMinVol, nudMinVol.Minimum, nudMinVol.Maximum);

            if (_sBasePicked)
            {
                _basePicked     = true;
                _baseX          = _sSavedBaseX;
                _baseY          = _sSavedBaseY;
                chkAutoBase.Checked = false;
                lblBase.Text      = $"X={_baseX:F3}  Y={_baseY:F3}";
                lblBase.ForeColor = SystemColors.ControlText;
            }
        }

        // ── Постоянное хранение настроек на диске ──────────────────────────────
        //    Файл: %APPDATA%\KartogrammaPlugin\settings.ini  (формат ключ=значение).
        //    Хранятся только настройки-предпочтения; геометрия, привязанная к
        //    конкретному чертежу (границы, базовая точка), НЕ сохраняется.
        private static readonly string _settingsFile =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KartogrammaPlugin", "settings.ini");

        private static bool _persistedLoaded = false;

        private static void EnsurePersistedLoaded()
        {
            if (_persistedLoaded) return;
            _persistedLoaded = true;   // пробуем только один раз за сессию
            try
            {
                if (!System.IO.File.Exists(_settingsFile)) return;

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in System.IO.File.ReadAllLines(_settingsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq)] = line.Substring(eq + 1);
                }

                decimal Dec(string k, decimal def) =>
                    map.TryGetValue(k, out var v) &&
                    decimal.TryParse(v, System.Globalization.NumberStyles.Any, inv, out var d) ? d : def;
                int Int(string k, int def) =>
                    map.TryGetValue(k, out var v) &&
                    int.TryParse(v, System.Globalization.NumberStyles.Any, inv, out var i) ? i : def;
                bool Bool(string k, bool def) =>
                    map.TryGetValue(k, out var v) && bool.TryParse(v, out var b) ? b : def;
                string? Str(string k) => map.TryGetValue(k, out var v) ? v : null;

                _sSurf1      = Str("Surf1") ?? _sSurf1;
                _sSurf2      = Str("Surf2") ?? _sSurf2;
                _sSurfIdx1   = Int("SurfIdx1", _sSurfIdx1);
                _sSurfIdx2   = Int("SurfIdx2", _sSurfIdx2);
                _sSizeX      = Dec("SizeX", _sSizeX);
                _sSizeY      = Dec("SizeY", _sSizeY);
                _sDontClip   = Bool("DontClip", _sDontClip);
                _sAutoBounds = Bool("AutoBounds", _sAutoBounds);
                _sAngType    = Int("AngType", _sAngType);
                _sAngle      = Dec("Angle", _sAngle);
                _sStyle      = Str("Style") ?? _sStyle;
                _sPrecIdx    = Int("PrecIdx", _sPrecIdx);
                _sHeight     = Dec("Height", _sHeight);
                _sDecIdx     = Int("DecIdx", _sDecIdx);
                _sHide       = Bool("Hide", _sHide);
                _sColDesign  = Int("ColDesign", _sColDesign);
                _sColExist   = Int("ColExist", _sColExist);
                _sColWork    = Int("ColWork", _sColWork);
                _sColVolume  = Int("ColVolume", _sColVolume);
                _sColTable   = Int("ColTable", _sColTable);
                _sTablePos   = Int("TablePos", _sTablePos);
                _sTblHeight  = Dec("TblHeight", _sTblHeight);
                _sTblStyle   = Str("TblStyle") ?? _sTblStyle;
                _sVolStyle   = Str("VolStyle") ?? _sVolStyle;
                _sVolPrecIdx = Int("VolPrecIdx", _sVolPrecIdx);
                _sVolHeight  = Dec("VolHeight", _sVolHeight);
                _sHideVol    = Bool("HideVol", _sHideVol);
                _sHideTable  = Bool("HideTable", _sHideTable);
                _sNodeStep   = Dec("NodeStep", _sNodeStep);
                _sVolMethodIdx = Int("VolMethodIdx", _sVolMethodIdx);
                _sMinVolChk  = Bool("MinVolChk", _sMinVolChk);
                _sMinVol     = Dec("MinVol", _sMinVol);

                _sessionSaved = true;   // есть сохранённые настройки → применяем их при открытии
            }
            catch { /* повреждённый файл игнорируем — останутся значения по умолчанию */ }
        }

        private static void PersistSettings()
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder();
                void P(string k, string v) => sb.Append(k).Append('=').Append(v).Append('\n');
                void PD(string k, decimal v) => P(k, v.ToString(inv));
                void PI(string k, int v) => P(k, v.ToString(inv));
                void PB(string k, bool v) => P(k, v ? "True" : "False");

                P("Surf1", _sSurf1 ?? "");
                P("Surf2", _sSurf2 ?? "");
                PI("SurfIdx1", _sSurfIdx1);
                PI("SurfIdx2", _sSurfIdx2);
                PD("SizeX", _sSizeX);
                PD("SizeY", _sSizeY);
                PB("DontClip", _sDontClip);
                PB("AutoBounds", _sAutoBounds);
                PI("AngType", _sAngType);
                PD("Angle", _sAngle);
                P("Style", _sStyle ?? "");
                PI("PrecIdx", _sPrecIdx);
                PD("Height", _sHeight);
                PI("DecIdx", _sDecIdx);
                PB("Hide", _sHide);
                PI("ColDesign", _sColDesign);
                PI("ColExist", _sColExist);
                PI("ColWork", _sColWork);
                PI("ColVolume", _sColVolume);
                PI("ColTable", _sColTable);
                PI("TablePos", _sTablePos);
                PD("TblHeight", _sTblHeight);
                P("TblStyle", _sTblStyle ?? "");
                P("VolStyle", _sVolStyle ?? "");
                PI("VolPrecIdx", _sVolPrecIdx);
                PD("VolHeight", _sVolHeight);
                PB("HideVol", _sHideVol);
                PB("HideTable", _sHideTable);
                PD("NodeStep", _sNodeStep);
                PI("VolMethodIdx", _sVolMethodIdx);
                PB("MinVolChk", _sMinVolChk);
                PD("MinVol", _sMinVol);

                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(_settingsFile)!);
                System.IO.File.WriteAllText(_settingsFile, sb.ToString());
            }
            catch { /* нет прав на запись — настройки останутся только в рамках сессии */ }
        }

        private static void SetComboByText(ComboBox cmb, string? text, int fallbackIdx)
        {
            if (text != null)
            {
                int idx = cmb.FindStringExact(text);
                if (idx >= 0) { cmb.SelectedIndex = idx; return; }
            }
            if (fallbackIdx >= 0 && fallbackIdx < cmb.Items.Count)
                cmb.SelectedIndex = fallbackIdx;
        }

        private static void SetPanelColor(Panel pnl, int aci)
        {
            pnl.Tag       = aci;
            pnl.BackColor = AciColorPickerForm.AciToColor(aci);
        }

        private static decimal Clamp(decimal v, decimal min, decimal max) =>
            Math.Max(min, Math.Min(max, v));

        // ════════════════════════════════════════════════════════════════════════
        //  Вспомогательные UI-методы
        // ════════════════════════════════════════════════════════════════════════

        private void OnColorClick(object? sender, EventArgs e)
        {
            if (sender is not Panel pnl) return;
            using var dlg = new AciColorPickerForm((int)(pnl.Tag ?? 1));
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                pnl.Tag       = dlg.SelectedAci;
                pnl.BackColor = AciColorPickerForm.AciToColor(dlg.SelectedAci);
            }
        }

        // Двухколоночная строка цвета
        private void AddColorRow2(GroupBox grp,
            string lbl1, ref Panel pnl1, int aci1,
            string lbl2, ref Panel pnl2, int aci2,
            int x, int rowY)
        {
            grp.Controls.Add(Lbl(lbl1, x, rowY + S(4)));
            pnl1          = ColorPanelNew(AciColorPickerForm.AciToColor(aci1));
            pnl1.Location = new Point(x + S(62), rowY);
            pnl1.Tag      = aci1;
            pnl1.Click   += OnColorClick;
            grp.Controls.Add(pnl1);

            // Вторая пара (Разница отметок / Объём) — плотно к первой:
            // подпись сразу после первой цветовой панели, затем её цвет-панель.
            grp.Controls.Add(Lbl(lbl2, x + S(104), rowY + S(4)));
            pnl2          = ColorPanelNew(AciColorPickerForm.AciToColor(aci2));
            pnl2.Location = new Point(x + S(220), rowY);
            pnl2.Tag      = aci2;
            pnl2.Click   += OnColorClick;
            grp.Controls.Add(pnl2);
        }

        private void AddActBtn(string text, int x, int y, int w, int h, EventHandler handler, string? tooltip = null)
        {
            var btn = new Button
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(w, h),
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 8.25f)
            };
            btn.Click += handler;
            if (tooltip != null) new ToolTip().SetToolTip(btn, tooltip);
            Controls.Add(btn);
        }

        private Panel ColorPanelNew(Color c) =>
            new Panel
            {
                Size        = new Size(S(30), S(18)),
                BackColor   = c,
                BorderStyle = BorderStyle.FixedSingle,
                Cursor      = Cursors.Hand
            };

        private static GroupBox Grp(string t, int x, int y, int w, int h)
        {
            var grp = new CenteredGroupBox { Text = t, Location = new Point(x, y), Size = new Size(w, h) };
            return grp;
        }

        /// <summary>GroupBox со скруглёнными углами по кривой Ламе (squircle) и текстом по центру</summary>
        private class CenteredGroupBox : GroupBox
        {
            private const double N     = 5.0; // степень суперэллипса (5 ≈ iOS squircle)
            private const int    RAD   = 10;   // радиус скругления
            private const int    STEPS = 20;   // точек на один угол

            // sign(v) * |v|^(2/n)
            private static float Sp(double v) =>
                (float)(Math.Sign(v) * Math.Pow(Math.Abs(v), 2.0 / N));

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                const int pad = 5;
                var textSize = TextRenderer.MeasureText(
                    g, Text, Font, Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int textW = textSize.Width;
                int textH = textSize.Height;
                int textX = (Width - textW) / 2;
                int lineY = textH / 2;

                float x0 = 0.5f, y0 = lineY + 0.5f;
                float x1 = Width - 1.5f, y1 = Height - 1.5f;
                float r = Math.Min(RAD, Math.Min((x1 - x0) / 2f, (y1 - y0) / 2f));

                // ── Замкнутый контур: 4 угла + 4 прямые стороны ──────────────
                var pts = new System.Collections.Generic.List<PointF>();

                // Левый верхний угол, центр (x0+r, y0+r): от (x0, y0+r) к (x0+r, y0)
                for (int i = 0; i <= STEPS; i++)
                {
                    double t = (Math.PI / 2.0) * i / STEPS;
                    pts.Add(new PointF(
                        x0 + r - r * Sp(Math.Cos(t)),
                        y0 + r - r * Sp(Math.Sin(t))));
                }
                // → верхняя прямая до правого верхнего угла

                // Правый верхний угол, центр (x1-r, y0+r): от (x1-r, y0) к (x1, y0+r)
                for (int i = 0; i <= STEPS; i++)
                {
                    double t = (Math.PI / 2.0) * i / STEPS;
                    pts.Add(new PointF(
                        x1 - r + r * Sp(Math.Sin(t)),
                        y0 + r - r * Sp(Math.Cos(t))));
                }
                // → правая прямая вниз

                // Правый нижний угол, центр (x1-r, y1-r): от (x1, y1-r) к (x1-r, y1)
                for (int i = 0; i <= STEPS; i++)
                {
                    double t = (Math.PI / 2.0) * i / STEPS;
                    pts.Add(new PointF(
                        x1 - r + r * Sp(Math.Cos(t)),
                        y1 - r + r * Sp(Math.Sin(t))));
                }
                // → нижняя прямая влево

                // Левый нижний угол, центр (x0+r, y1-r): от (x0+r, y1) к (x0, y1-r)
                for (int i = 0; i <= STEPS; i++)
                {
                    double t = (Math.PI / 2.0) * i / STEPS;
                    pts.Add(new PointF(
                        x0 + r - r * Sp(Math.Sin(t)),
                        y1 - r + r * Sp(Math.Cos(t))));
                }
                // → левая прямая вверх, замыкание

                // Рисуем замкнутый контур
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddPolygon(pts.ToArray());
                using var pen = new Pen(SystemColors.ControlDark, 1f);
                g.DrawPath(pen, path);

                // Стираем линию рамки в месте текста
                using var bg = new SolidBrush(BackColor);
                g.FillRectangle(bg, textX - pad, 0, textW + 2 * pad, lineY + 2);

                // Текст — точно по центру
                TextRenderer.DrawText(
                    g, Text, Font,
                    new Rectangle(textX, 0, textW, textH),
                    ForeColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        private static Label Lbl(string t, int x, int y) =>
            new Label { Text = t, Location = new Point(x, y), AutoSize = true };

        private Button DeadBtn(string t, int x, int y) =>
            new Button { Text = t, Location = new Point(x, y), Size = new Size(S(32), S(22)), Enabled = false };

        private static ComboBox Cmb(IList<string> items, int def, Point loc, Size sz)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = loc, Size = sz };
            c.Items.AddRange(new List<string>(items).ToArray());
            if (def >= 0 && def < items.Count) c.SelectedIndex = def;
            return c;
        }

        private static NumericUpDown Nud(decimal min, decimal max, decimal val, int dec, decimal inc) =>
            new NumericUpDown { Minimum = min, Maximum = max, Value = val, DecimalPlaces = dec, Increment = inc };
    }
}
