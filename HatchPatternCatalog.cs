using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KartogrammaPlugin
{
    /// <summary>Описание одного семейства линий образца штриховки из .pat-файла.</summary>
    internal sealed class HatchPatternLine
    {
        public double   Angle;    // градусы
        public double   OriginX, OriginY;
        public double   DeltaX;   // сдвиг вдоль линии (для штриховых образцов)
        public double   DeltaY;   // расстояние между параллельными линиями
        public double[] Dashes = Array.Empty<double>();  // + штрих, − пробел, 0 точка
    }

    /// <summary>Образец штриховки: имя, описание и семейства линий.</summary>
    internal sealed class HatchPatternDef
    {
        public string Name        = "";
        public string Description = "";
        public readonly List<HatchPatternLine> Lines = new();
    }

    /// <summary>
    /// Палитра образцов штриховки AutoCAD.
    ///
    /// Список и геометрия берутся из штатного .pat-файла AutoCAD — тогда в
    /// плагине видны ровно те же образцы, что и в самом AutoCAD, а окошко
    /// предпросмотра рисует их по настоящим определениям, а не «на глаз».
    /// Предпочитается acadiso.pat (метрический), затем acad.pat.
    ///
    /// Если файл не найден (AutoCAD установлен нестандартно, урезанная
    /// поставка), используется встроенный набор самых ходовых образцов —
    /// плагин остаётся работоспособным, просто палитра короче.
    /// </summary>
    internal static class HatchPatternCatalog
    {
        private static readonly object _lock = new();
        private static Dictionary<string, HatchPatternDef>? _byName;
        private static List<string>? _names;
        private static bool _preferMetric = true;

        /// <summary>Откуда прочитана палитра (для диагностики в окне настроек).</summary>
        internal static string? SourceFile { get; private set; }

        /// <summary>
        /// Задать, каким файлом образцов пользуется ЧЕРТЁЖ. AutoCAD выбирает его
        /// по системной переменной MEASUREMENT: 1 — acadiso.pat, 0 — acad.pat.
        /// Разница не косметическая: у ANSI31 шаг 3.175 против 0.125, то есть
        /// в 25.4 раза. Если предпросмотр читает не тот файл, что чертёж, он
        /// показывает читаемую штриховку там, где на чертеже будет сплошная
        /// заливка. Поэтому палитра обязана совпадать с чертежом.
        /// </summary>
        internal static void Configure(bool metric)
        {
            lock (_lock)
            {
                if (_preferMetric == metric && _byName != null) return;
                _preferMetric = metric;
                _byName    = null;
                _names     = null;
                SourceFile = null;
            }
        }

        /// <summary>Имена образцов по алфавиту. SOLID всегда первым.</summary>
        internal static IReadOnlyList<string> Names
        {
            get { EnsureLoaded(); return _names!; }
        }

        internal static HatchPatternDef? Find(string name)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _byName!.TryGetValue(name.Trim().ToUpperInvariant(), out var d) ? d : null;
        }

        // ── Загрузка ──────────────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (_byName != null) return;
            lock (_lock)
            {
                if (_byName != null) return;

                var map = new Dictionary<string, HatchPatternDef>(StringComparer.OrdinalIgnoreCase);
                string? src = null;

                foreach (var file in CandidateFiles())
                {
                    try
                    {
                        var parsed = Parse(File.ReadAllLines(file));
                        if (parsed.Count == 0) continue;
                        foreach (var p in parsed) map[p.Name.ToUpperInvariant()] = p;
                        src = file;
                        break;
                    }
                    catch { /* нечитаемый файл — пробуем следующий */ }
                }

                if (map.Count == 0)
                    foreach (var p in BuiltIn())
                        map[p.Name.ToUpperInvariant()] = p;

                // SOLID в .pat не описан — это встроенная сплошная заливка.
                if (!map.ContainsKey("SOLID"))
                    map["SOLID"] = new HatchPatternDef
                        { Name = "SOLID", Description = "Сплошная заливка" };

                var names = map.Values.Select(v => v.Name)
                                      .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                      .ToList();
                names.Remove("SOLID");
                names.Insert(0, "SOLID");

                SourceFile = src;
                _byName    = map;
                _names     = names;
            }
        }

        private static IEnumerable<string> CandidateFiles()
        {
            var roots = new List<string>();

            void AddRoot(string? p)
            {
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) roots.Add(p!);
            }

            AddRoot(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk"));
            AddRoot(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk"));
            AddRoot(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk"));

            // Порядок задаётся чертежом (см. Configure), а не нашими вкусами.
            var order = _preferMetric
                ? new[] { "acadiso.pat", "acad.pat" }
                : new[] { "acad.pat", "acadiso.pat" };

            foreach (var pattern in order)
            foreach (var root in roots)
            {
                string[] hits;
                try
                {
                    hits = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
                }
                catch { continue; }

                // Более свежая установка обычно лежит глубже и новее — берём
                // самый свежий файл, чтобы попасть в актуальную версию AutoCAD.
                foreach (var h in hits.OrderByDescending(File.GetLastWriteTimeUtc))
                    yield return h;
            }
        }

        /// <summary>
        /// Разбор .pat: строка «*ИМЯ, описание» открывает образец, следующие
        /// числовые строки — семейства линий «угол, x, y, dx, dy [, штрихи…]».
        /// </summary>
        internal static List<HatchPatternDef> Parse(IEnumerable<string> lines)
        {
            var res = new List<HatchPatternDef>();
            HatchPatternDef? cur = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;

                if (line[0] == '*')
                {
                    if (cur != null && cur.Lines.Count > 0) res.Add(cur);
                    int comma = line.IndexOf(',');
                    cur = new HatchPatternDef
                    {
                        Name        = (comma < 0 ? line.Substring(1) : line.Substring(1, comma - 1)).Trim(),
                        Description = comma < 0 ? "" : line.Substring(comma + 1).Trim()
                    };
                    continue;
                }

                if (cur == null) continue;

                var parts = line.Split(',');
                if (parts.Length < 5) continue;

                var nums = new List<double>(parts.Length);
                bool ok = true;
                foreach (var p in parts)
                {
                    var t = p.Trim();
                    if (t.Length == 0) { ok = false; break; }
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    { ok = false; break; }
                    nums.Add(v);
                }
                if (!ok || nums.Count < 5) continue;

                cur.Lines.Add(new HatchPatternLine
                {
                    Angle   = nums[0],
                    OriginX = nums[1],
                    OriginY = nums[2],
                    DeltaX  = nums[3],
                    DeltaY  = nums[4],
                    Dashes  = nums.Count > 5 ? nums.Skip(5).ToArray() : Array.Empty<double>()
                });
            }

            if (cur != null && cur.Lines.Count > 0) res.Add(cur);
            return res;
        }

        /// <summary>Запасной набор — самые ходовые образцы, если .pat не найден.</summary>
        private static IEnumerable<HatchPatternDef> BuiltIn()
        {
            HatchPatternDef P(string name, string descr, params HatchPatternLine[] ls)
            {
                var d = new HatchPatternDef { Name = name, Description = descr };
                d.Lines.AddRange(ls);
                return d;
            }
            HatchPatternLine L(double a, double ox, double oy, double dx, double dy,
                               params double[] dash)
                => new() { Angle = a, OriginX = ox, OriginY = oy,
                           DeltaX = dx, DeltaY = dy, Dashes = dash };

            yield return P("ANSI31", "Штриховка 45°", L(45, 0, 0, 0, 3.175));
            yield return P("ANSI32", "Сталь",         L(45, 0, 0, 0, 9.525),
                                                     L(45, 4.4979, 0, 0, 9.525));
            yield return P("ANSI37", "Крест-накрест", L(45, 0, 0, 0, 3.175),
                                                     L(135, 0, 0, 0, 3.175));
            yield return P("NET",    "Сетка",         L(0, 0, 0, 0, 3.175),
                                                     L(90, 0, 0, 0, 3.175));
            yield return P("LINE",   "Параллельные",  L(0, 0, 0, 0, 3.175));
            yield return P("DOTS",   "Точки",         L(0, 0, 0, 3.175, 1.5875, 0, -3.175));
            yield return P("EARTH",  "Грунт",         L(0, 0, 0, 6.35, 6.35, 6.35, -1.5875),
                                                     L(90, 0, 0, 6.35, 6.35, 6.35, -1.5875));
            yield return P("GRAVEL", "Гравий",        L(0, 0, 0, 4.7625, 4.7625, 1.5875, -1.5875));
            yield return P("CROSS",  "Крестики",      L(0, 0, 0, 4.7625, 4.7625, 1.5875, -3.175),
                                                     L(90, 1.5875, -1.5875, 4.7625, 4.7625, 1.5875, -3.175));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Предпросмотр образца в окошке
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Нарисовать образец в прямоугольнике превью по НАСТОЯЩЕМУ определению
        /// из палитры. Масштаб влияет на густоту так же, как в AutoCAD: вдвое
        /// больший масштаб — вдвое реже линии.
        /// </summary>
        /// <param name="pixelsPerUnit">Сколько пикселей приходится на единицу
        /// чертежа. Задаётся вызывающим кодом из размера ячейки, чтобы окошко
        /// показывало ровно ту густоту, что получится на чертеже.</param>
        internal static void Paint(Graphics g, Rectangle box, string patternName,
            double angleDeg, double scale, Color color, double pixelsPerUnit)
        {
            var oldClip   = g.Clip;
            var oldSmooth = g.SmoothingMode;
            try
            {
                g.SetClip(box);

                if (string.Equals(patternName, "SOLID", StringComparison.OrdinalIgnoreCase))
                {
                    using var brush = new SolidBrush(color);
                    g.FillRectangle(brush, box);
                    return;
                }

                var def = Find(patternName);
                if (def == null || def.Lines.Count == 0)
                {
                    // Неизвестный образец — честно показываем, что рисовать нечем.
                    using var pen0 = new Pen(color);
                    g.DrawLine(pen0, box.Left, box.Bottom - 1, box.Right - 1, box.Top);
                    return;
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (scale <= 0) scale = 1.0;

                double cx = box.Left + box.Width / 2.0;
                double cy = box.Top + box.Height / 2.0;
                double reach = Math.Sqrt(box.Width * (double)box.Width
                                       + box.Height * (double)box.Height);

                foreach (var fam in def.Lines)
                {
                    // Шаг между параллельными линиями в пикселях. Если линии
                    // сходятся плотнее пикселя, честно показываем заливку —
                    // именно так эта штриховка и будет выглядеть на чертеже.
                    double stepPx = Math.Abs(fam.DeltaY) * scale * pixelsPerUnit;
                    if (stepPx < 1.0)
                    {
                        using var solid = new SolidBrush(color);
                        g.FillRectangle(solid, box);
                        continue;
                    }

                    double a  = (fam.Angle + angleDeg) * Math.PI / 180.0;
                    double ux = Math.Cos(a),  uy = Math.Sin(a);   // вдоль линии
                    double vx = -Math.Sin(a), vy = Math.Cos(a);   // поперёк

                    using var pen = new Pen(color, 1f);
                    ApplyDashes(pen, fam, scale, pixelsPerUnit);

                    int half = (int)Math.Ceiling(reach / stepPx) + 1;
                    if (half > 200) half = 200;   // защита от кратчайших шагов

                    double offPxX = fam.OriginX * scale * pixelsPerUnit;
                    double offPxY = fam.OriginY * scale * pixelsPerUnit;
                    double slidePx = fam.DeltaX * scale * pixelsPerUnit;

                    for (int k = -half; k <= half; k++)
                    {
                        // Начало k-й линии: сдвиг поперёк на k·ΔY и вдоль на k·ΔX
                        double bx = cx + offPxX + k * (stepPx * vx + slidePx * ux);
                        // Экранный Y растёт вниз — образец рисуем в «чертёжной»
                        // ориентации, поэтому по Y знак меняем.
                        double by = cy - (offPxY + k * (stepPx * vy + slidePx * uy));

                        g.DrawLine(pen,
                            (float)(bx - ux * reach), (float)(by + uy * reach),
                            (float)(bx + ux * reach), (float)(by - uy * reach));
                    }
                }
            }
            catch { /* предпросмотр не должен ронять окно настроек */ }
            finally
            {
                g.SmoothingMode = oldSmooth;
                g.Clip = oldClip;
            }
        }

        private static void ApplyDashes(Pen pen, HatchPatternLine fam,
            double scale, double pixelsPerUnit)
        {
            if (fam.Dashes.Length == 0) return;

            var pat = new List<float>();
            foreach (var d in fam.Dashes)
            {
                // 0 — точка: рисуем очень коротким штрихом, иначе GDI+ бракует шаблон
                double len = Math.Abs(d) * scale * pixelsPerUnit;
                if (len < 0.4) len = 0.4;
                pat.Add((float)len);
            }
            // GDI+ требует чётное число элементов: штрих-пробел-штрих-пробел…
            if (pat.Count % 2 != 0) pat.Add(pat[pat.Count - 1]);

            float sum = 0; foreach (var v in pat) sum += v;
            if (sum <= 0) return;

            try
            {
                pen.DashStyle   = DashStyle.Custom;
                pen.DashPattern = pat.ToArray();
            }
            catch { pen.DashStyle = DashStyle.Solid; }
        }
    }
}
