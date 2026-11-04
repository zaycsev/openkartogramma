using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: InternalsVisibleTo("KartogrammaTests")]

namespace KartogrammaPlugin
{
    internal sealed class CellData
    {
        public int    Row, Col;
        public double ExistElev;
        public double DesignElev;
        public double WorkHeight;   // design − exist  (+ насыпь, − выемка)
        public double Volume;       // объём ячейки (+ насыпь, − выемка)
        public bool   HasData;
        public bool   IsCut => WorkHeight < 0;  // выемка: design < exist
    }

    public sealed class KartogrammaProcessor
    {
        private readonly Document           _doc;
        private readonly KartogrammaOptions _o;
        private readonly Database           _db;
        private readonly Action<string, int>? _progress;

        private BlockTableRecord _ms     = null!;
        private ObjectId         _tsId;
        private ObjectId         _tblTsId;  // стиль текста для итоговой таблицы
        private List<Point2d>?   _boundaryPts;  // кеш вершин наружной границы
        private List<List<Point2d>>? _innerPtsList;  // кеш вершин внутренних «дырок»
        private bool             _clipErrorReported;   // чтобы не спамить лог

        public KartogrammaProcessor(Document doc, KartogrammaOptions opts,
            Action<string, int>? progress = null)
        {
            _doc      = doc;
            _o        = opts;
            _db       = doc.Database;
            _progress = progress;
        }

        private void Report(string msg, int percent)
        {
            _progress?.Invoke(msg, percent);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Построить только сетку квадратов (без отметок и объёмов)
        // ═══════════════════════════════════════════════════════════════════════
        public void BuildGrid()
        {
            var ed = _doc.Editor;
            try
            {
                var civilDoc = CivilApplication.ActiveDocument;
                using var trans = _db.TransactionManager.StartTransaction();

                if (!FindSurfaces(trans, civilDoc, out var surf1, out var surf2))
                {
                    ed.WriteMessage("\n[Картограмма] Ошибка: поверхности не найдены.");
                    return;
                }

                // Снимки TIN в память — до всех циклов с отметками
                PrepareElevationCache(surf1!, surf2!);

                // Границы грузим ДО CalcAutoGrid — они задают габариты сетки.
                LoadManualBoundaries(trans);

                if (_o.AutoBasePoint) CalcAutoBasePoint(surf1!, surf2!);

                var tst = (TextStyleTable)trans.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
                _tsId = tst.Has(_o.TextStyleName) ? tst[_o.TextStyleName] : _db.Textstyle;

                CalcAutoGrid(surf1!, surf2!, out int rows, out int cols);
                ed.WriteMessage($"\n[Картограмма] Базовая точка: X={_o.BaseX:F2}, Y={_o.BaseY:F2}");
                ed.WriteMessage($"\n[Картограмма] Сетка: {cols} столбцов × {rows} строк");

                double angle = _o.RotationRadians;
                double cosA  = Math.Cos(angle), sinA = Math.Sin(angle);

                EnsureLayer(trans, _o.GridLayerName, 7);

                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                _ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Кресты ручных троек остаются — сами тройки здесь не удаляются.
                EraseByLayer(trans, _ms, _o.GridLayerName, e => !IsManualCross(e));
                int drawn = DrawGridLines(trans, rows, cols, cosA, sinA, surf1!, surf2!);

                // Зоны резких перепадов (зумпф и т.п.) — рамки на слое сетки
                DetectAnomalyZones(surf1!, surf2!, rows, cols, cosA, sinA);
                if (_anomalyZones != null)
                    foreach (var z in _anomalyZones)
                        DrawZoneRect(trans, z, cosA, sinA);

                trans.Commit();

                ed.WriteMessage($"\n[Картограмма] Сетка построена: {drawn} из {cols * rows} ячеек (в зоне перекрытия поверхностей)\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Подписать отметки + рассчитать объёмы (метод триангуляции)
        // ═══════════════════════════════════════════════════════════════════════
        public void CalculateVolume()
        {
            var ed = _doc.Editor;
            try
            {
                var civilDoc = CivilApplication.ActiveDocument;
                using var trans = _db.TransactionManager.StartTransaction();

                if (!FindSurfaces(trans, civilDoc, out var surf1, out var surf2))
                {
                    ed.WriteMessage("\n[Картограмма] Ошибка: поверхности не найдены.");
                    return;
                }

                // Снимки TIN в память — до всех циклов с отметками
                PrepareElevationCache(surf1!, surf2!);

                // Границы грузим ДО CalcAutoGrid — они задают габариты сетки.
                LoadManualBoundaries(trans);

                if (_o.AutoBasePoint) CalcAutoBasePoint(surf1!, surf2!);

                var tst = (TextStyleTable)trans.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
                _tsId    = tst.Has(_o.TextStyleName)      ? tst[_o.TextStyleName]      : _db.Textstyle;
                _tblTsId = tst.Has(_o.TableTextStyleName) ? tst[_o.TableTextStyleName] : _db.Textstyle;

                CalcAutoGrid(surf1!, surf2!, out int rows, out int cols);

                double angle = _o.RotationRadians;
                double cosA  = Math.Cos(angle), sinA = Math.Sin(angle);
                double area  = _o.CellSizeX * _o.CellSizeY;

                // Зоны резких перепадов (зумпф и т.п.) ищем ДО расчёта объёмов:
                // их объём исключается из обычных ячеек и считается отдельно.
                Report("Анализ резких перепадов…", 8);
                DetectAnomalyZones(surf1!, surf2!, rows, cols, cosA, sinA);

                EnsureLayer(trans, _o.TextLayerName,   7);
                EnsureLayer(trans, _o.WorkLayerName,   _o.ColorWork);
                EnsureLayer(trans, _o.ExistLayerName,  _o.ColorExisting);
                EnsureLayer(trans, _o.DesignLayerName, _o.ColorDesign);
                EnsureLayer(trans, _o.VolumeLayerName, _o.ColorVolume);
                EnsureLayer(trans, _o.TableLayerName,  _o.ColorTable);
                EnsureLayer(trans, _o.HatchLayerName,    _o.HatchCut.ColorAci);
                EnsureLayer(trans, _o.ZeroLineLayerName, _o.ZeroLine.ColorAci);

                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                _ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Удаляем старые объекты перед перерисовкой
                EnsureLayer(trans, _o.GridLayerName, 7);
                EraseByLayer(trans, _ms, _o.GridLayerName);
                EraseByLayer(trans, _ms, _o.TextLayerName);
                EraseByLayer(trans, _ms, _o.WorkLayerName);
                EraseByLayer(trans, _ms, _o.ExistLayerName);
                EraseByLayer(trans, _ms, _o.DesignLayerName);
                EraseByLayer(trans, _ms, _o.VolumeLayerName);
                EraseByLayer(trans, _ms, _o.TableLayerName);
                EraseByLayer(trans, _ms, _o.HatchLayerName);
                EraseByLayer(trans, _ms, _o.ZeroLineLayerName);

                // Наружная граница и внутренние «дырки» уже загружены выше в
                // LoadManualBoundaries (_boundaryPts / _innerPtsList) — до CalcAutoGrid.

                // === Перестроение сетки (только в зоне перекрытия поверхностей) ===
                Report("Построение сетки…", 2);
                DrawGridLines(trans, rows, cols, cosA, sinA, surf1!, surf2!);

                var cells = BuildCells(surf1!, surf2!, rows, cols, cosA, sinA, area);

                // Подписи объёмов ячеек + подсчёт итогов (точный граничный расчёт)
                // Считаем только ячейки с данными (перекрытие поверхностей)
                var dataCells = cells.Where(cl => cl.HasData).ToList();
                double totCut = 0, totFill = 0;
                var colCut  = new double[cols];
                var colFill = new double[cols];
                var rowCut  = new double[rows];
                var rowFill = new double[rows];

                int totalCells = dataCells.Count;
                {
                    long nLog = SubgridWanted();
                    ed.WriteMessage(nLog > MaxSubgridN
                        ? $"\n[Картограмма] Расчёт объёмов: шаг {StepText()} м — мельче {MaxSubgridN} узлов на сторону ячейки, " +
                          "объёмы считаются точно по треугольникам TIN (предел при шаге → 0)..."
                        : $"\n[Картограмма] Расчёт объёмов: шаг {StepText()} м → N={nLog} на ячейку...");
                    _exactVolCells = _cappedVolCells = 0;
                    ed.WriteMessage($"\n[Картограмма] Ячеек с данными: {totalCells} из {rows * cols}");
                    Report($"Расчёт объёмов ({totalCells} ячеек)…", 10);
                }

                // ── Фаза 1: численное интегрирование объёмов ──────────────────────
                // Ячейки независимы. Когда обе поверхности — TIN со снимком в
                // памяти (PrepareElevationCache), расчёт чисто вычислительный, без
                // COM-вызовов AutoCAD — можно параллелить по ядрам. Прогресс
                // репортим из основного потока между чанками. Фолбэк (GridSurface
                // и т.п.) идёт последовательно — Civil API не потокобезопасен.
                var volRes  = new double[totalCells];
                var areaRes = new double[totalCells];
                // Раздельные объёмы насыпи и выемки внутри ячейки — нужны там,
                // где через ячейку проходит нулевая линия (см. ZeroLineSplit).
                var fillRes = new double[totalCells];
                var cutRes  = new double[totalCells];
                // Площади насыпной и выемочной частей ячейки — для строк
                // «Площадь выемки/насыпи» под итоговой таблицей.
                var fillAreaRes = new double[totalCells];
                var cutAreaRes  = new double[totalCells];

                void ComputeCell(int i)
                {
                    var dc = dataCells[i];
                    // Объём: два метода по выбору пользователя.
                    //   Triangulation — субтреугольная разбивка, точно.
                    //   Squares       — классический ручной метод S×(h1+h2+h3+h4)/4
                    //                   по отметкам в 4 углах ячейки.
                    // Площадь (для Triangulation) берём из ТОЙ ЖЕ субтреугольной
                    // интеграции, что и объём — она согласована с объёмом и
                    // совпадает с площадью из «пульта объёмов» Civil.
                    if (_o.VolumeMethod == VolumeMethod.Squares)
                    {
                        volRes[i]  = CalcCellVolumeSquares(dc.Row, dc.Col, surf1!, surf2!,
                            cosA, sinA, out fillRes[i], out cutRes[i],
                            out fillAreaRes[i], out cutAreaRes[i]);
                        areaRes[i] = CalcCellEffectiveArea(dc.Row, dc.Col, surf1!, surf2!, cosA, sinA);

                        // Доли насыпи/выемки — от площади, идущей в «Площадь:»,
                        // чтобы площадь выемки + насыпи сходилась с общей.
                        double split = fillAreaRes[i] + cutAreaRes[i];
                        if (split > 0)
                        {
                            double k = areaRes[i] / split;
                            fillAreaRes[i] *= k;
                            cutAreaRes[i]  *= k;
                        }
                    }
                    else
                    {
                        volRes[i] = CalcCellVolumeAccurate(dc.Row, dc.Col, surf1!, surf2!,
                            cosA, sinA, out areaRes[i], out fillRes[i], out cutRes[i],
                            out fillAreaRes[i], out cutAreaRes[i]);
                    }
                }

                bool canParallel = _snap1 != null && _snap2 != null;
                const int Chunk = 64;
                for (int start = 0; start < totalCells; start += Chunk)
                {
                    int end = Math.Min(start + Chunk, totalCells);
                    if (canParallel)
                        System.Threading.Tasks.Parallel.For(start, end, ComputeCell);
                    else
                        for (int i = start; i < end; i++) ComputeCell(i);

                    Report($"Объём: {end}/{totalCells} ячеек…",
                        10 + (int)(80.0 * end / totalCells));
                }
                if (_exactVolCells > 0)
                    ed.WriteMessage($"\n[Картограмма] Объём точно по треугольникам TIN: {_exactVolCells} ячеек");
                if (_cappedVolCells > 0)
                    ed.WriteMessage(
                        $"\n[Картограмма] В {_cappedVolCells} ячейк(ах) точный расчёт неприменим (не TIN, заданная граница " +
                        $"или зона перепада) — шаг субсетки ограничен {MaxSubgridN} узлами на сторону " +
                        $"({Math.Max(_o.CellSizeX, _o.CellSizeY) / MaxSubgridN * 1000:0.###} мм)");

                // Отладка распределения: KARTOGRAMMA_DEBUG=1 → объём и площадь
                // каждой ячейки в командную строку (для сверки версий по ячейкам)
                if (Environment.GetEnvironmentVariable("KARTOGRAMMA_DEBUG") == "1")
                    for (int ci = 0; ci < totalCells; ci++)
                        ed.WriteMessage(
                            $"\n[Отладка] Ячейка строка {dataCells[ci].Row + 1}, колонка {dataCells[ci].Col + 1}: " +
                            $"V={volRes[ci]:F3} м³, S={areaRes[ci]:F2} м², " +
                            $"насыпь={fillRes[ci]:F3}, выемка={cutRes[ci]:F3}");

                // Штриховка выемки/насыпи и линия нулевых работ — по тем же
                // ячейкам и по тому же признаку знака рабочей отметки, по
                // которому делятся объёмы. Идёт ПОСЛЕ фазы 1: площади насыпи и
                // выемки ячейки (субтреугольники с шагом VolumeNodeStep) точно
                // говорят, есть ли в ней второй знак — даже небольшое пятно,
                // которое проскочит между точками грубой разведки. И ДО подписей —
                // чтобы штриховка лежала под цифрами.
                var signAreas = new Dictionary<(int, int), (double Fill, double Cut)>(totalCells);
                for (int ci = 0; ci < totalCells; ci++)
                    signAreas[(dataCells[ci].Row, dataCells[ci].Col)] = (fillAreaRes[ci], cutAreaRes[ci]);
                // В методе «Квадраты» площади насыпи/выемки — оценка по четырём
                // углам ячейки, а не по поверхности: штриховку (и сверку) ведём
                // только по точным площадям триангуляции.
                DrawHatchAndZeroLine(trans, rows, cols, cosA, sinA, surf1!, surf2!,
                    _o.VolumeMethod == VolumeMethod.Triangulation ? signAreas : null);

                // === Подписи отметок (чёрная / красная / рабочая) ===
                // Отметки рисуются в каждом узле сетки (rows+1)×(cols+1).
                // Узлы вне клип-области пропускаются (метки на «оторванных»
                // узлах физического смысла не имеют).
                Report("Построение подписей отметок…", 91);
                _labelBoxes.Clear();
                EnsureVolPosRegApp(trans);
                for (int nr = 0; nr <= rows; nr++)
                for (int nc = 0; nc <= cols; nc++)
                    DrawNodeLabel(trans, nr, nc, rows, cols, cosA, sinA, surf1!, surf2!);

                // Дополнительные тройки в точках пересечения границ с линиями
                // сетки — это «углы» образованные обрезкой. Только в режиме
                // «Обрезать»: в «не обрезать» квадраты целые, новых углов нет.
                if (_o.ClipCells)
                    DrawBoundaryGridIntersectionLabels(trans, rows, cols, cosA, sinA, surf1!, surf2!);

                // ── Фаза 2: подписи и итоги (последовательно — работа с чертежом) ──
                double totalArea = 0, totalCutArea = 0, totalFillArea = 0;
                for (int ci = 0; ci < totalCells; ci++)
                {
                    var dc = dataCells[ci];
                    int r = dc.Row, c = dc.Col;
                    double vol = volRes[ci], cellArea = areaRes[ci];

                    // Площадь перекрытия суммируем независимо от порога объёма
                    // (как в Civil — площадь не зависит от отсечения малых объёмов).
                    totalArea     += cellArea;
                    totalCutArea  += cutAreaRes[ci];
                    totalFillArea += fillAreaRes[ci];

                    double volFill = fillRes[ci], volCut = cutRes[ci];

                    // ── Ячейка с нулевой линией ───────────────────────────────
                    // Если через ячейку проходит линия нулевых работ, в ней
                    // есть и насыпь, и выемка. Сальдо их гасит и скрывает
                    // реальный объём работ, поэтому такие ячейки подписываются
                    // ДВУМЯ цифрами — каждая в своей части ячейки — и в итоги
                    // идут обеими частями. Даже небольшая часть второго знака
                    // подписывается отдельно — фильтр MinVolume здесь не
                    // действует (он отсекает мелкие ЯЧЕЙКИ, а не части).
                    //
                    // Нижняя граница — половина последнего разряда: меньшая
                    // часть округлится до нуля, и по всему чертежу пошли бы
                    // пары вида «+0.00».
                    double minShow = 0.5 * Math.Pow(10, -_o.VolumePrecision);
                    bool splitCell = volFill >= minShow
                                     && Math.Abs(volCut) >= minShow;

                    if (!splitCell && vol == 0.0) continue;   // ячейка полностью вне зоны

                    double absVol = Math.Abs(vol);
                    if (!splitCell && absVol < _o.MinVolume) continue;

                    // vol > 0 = насыпь (design > exist), vol < 0 = выемка
                    bool isCut = vol < 0;
                    int aci    = _o.ColorVolume;

                    double x0 = c * _o.CellSizeX;
                    double y0 = r * _o.CellSizeY;
                    double vh = _o.VolumeTextHeight;

                    // Округляем до точности отображения ДО суммирования, чтобы
                    // строчные/столбцовые/общие итоги совпадали с тем, что нарисовано
                    // в каждой ячейке (иначе двойное округление даёт расхождение
                    // на ±1 единицу младшего разряда: 0.704+0.026=0.730→"-0.73",
                    // а сумма видимых "-0.70"+"-0.02" = "-0.72").
                    // Парсим строку обратно — гарантированно совпадает с тем, что
                    // выведет ToString("Fn") в подписи ячейки.
                    double Disp(double v) => double.Parse(
                        v.ToString("F" + _o.VolumePrecision,
                            System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.CultureInfo.InvariantCulture);

                    if (splitCell)
                    {
                        // Каждая цифра — в центре своей части ячейки, чтобы
                        // читалось, где насыпь, а где выемка.
                        FindZeroLineAnchors(r, c, cosA, sinA, surf1!, surf2!,
                            out double fLx, out double fLy,
                            out double cLx, out double cLy);

                        if (_o.ShowVolumeArea)
                        {
                            // «Площадь и объём»: в каждой части свой круг —
                            // объём и площадь насыпи / выемки.
                            PlaceVolumeAreaLabel(trans, Signed(volFill, _o.VolumePrecision),
                                Area(fillAreaRes[ci]), vh, angle, aci,
                                x0, y0, _o.CellSizeX, _o.CellSizeY, fLx, fLy,
                                PartPredicate(VolLabelKind.Fill, surf1, surf2));
                            PlaceVolumeAreaLabel(trans, Signed(volCut, _o.VolumePrecision),
                                Area(cutAreaRes[ci]), vh, angle, aci,
                                x0, y0, _o.CellSizeX, _o.CellSizeY, cLx, cLy,
                                PartPredicate(VolLabelKind.Cut, surf1, surf2));
                        }
                        else
                        {
                        // Не наезжая на тройки отметок: ближайшее к центру
                        // своей части свободное место в пределах этой части.
                        PlaceVolumeLabel(AddCenteredText(trans, _o.VolumeLayerName,
                            LW(fLx, fLy, cosA, sinA),
                            Signed(volFill, _o.VolumePrecision),
                            vh, angle, aci, hideMask: _o.HideMaskVolume),
                            x0, y0, _o.CellSizeX, _o.CellSizeY, fLx, fLy,
                            PartPredicate(VolLabelKind.Fill, surf1, surf2), VolLabelKind.Fill);
                        PlaceVolumeLabel(AddCenteredText(trans, _o.VolumeLayerName,
                            LW(cLx, cLy, cosA, sinA),
                            Signed(volCut, _o.VolumePrecision),
                            vh, angle, aci, hideMask: _o.HideMaskVolume),
                            x0, y0, _o.CellSizeX, _o.CellSizeY, cLx, cLy,
                            PartPredicate(VolLabelKind.Cut, surf1, surf2), VolLabelKind.Cut);
                        }

                        double dFill = Disp(volFill), dCut = Disp(Math.Abs(volCut));
                        totFill += dFill; colFill[c] += dFill; rowFill[r] += dFill;
                        totCut  += dCut;  colCut[c]  += dCut;  rowCut[r]  += dCut;
                        continue;
                    }

                    // Объём — по центру ячейки; для обрезанных ячеек подбираем
                    // ближайшую точку внутри клип-области, чтобы метка не
                    // попала за пределы оставшейся после обрезки геометрии.
                    double cx = x0 + _o.CellSizeX * 0.5;
                    double cy = y0 + _o.CellSizeY * 0.5;
                    FindInsideAnchor(x0, y0, _o.CellSizeX, _o.CellSizeY,
                        cx, cy, cosA, sinA, surf1!, surf2!, out double vLx, out double vLy);
                    // Не наезжая на тройки отметок: ближайшее к центру свободное
                    // место в видимой части ячейки.
                    if (_o.ShowVolumeArea)
                        PlaceVolumeAreaLabel(trans, Signed(vol, _o.VolumePrecision),
                            Area(cellArea), vh, angle, aci,
                            x0, y0, _o.CellSizeX, _o.CellSizeY, vLx, vLy,
                            PartPredicate(VolLabelKind.Cell, surf1, surf2));
                    else
                    PlaceVolumeLabel(AddCenteredText(trans, _o.VolumeLayerName,
                        LW(vLx, vLy, cosA, sinA),
                        Signed(vol, _o.VolumePrecision),
                        vh, angle, aci, hideMask: _o.HideMaskVolume),
                        x0, y0, _o.CellSizeX, _o.CellSizeY, vLx, vLy,
                        PartPredicate(VolLabelKind.Cell, surf1, surf2), VolLabelKind.Cell);

                    double dispVol = Disp(absVol);
                    if (isCut) { totCut  += dispVol; colCut[c]  += dispVol; rowCut[r]  += dispVol; }
                    else       { totFill += dispVol; colFill[c] += dispVol; rowFill[r] += dispVol; }
                }

                // ── Зоны резких перепадов: рамка + отдельный объём ────────────────
                // В итоги таблицы объём зоны относится к строке/колонке ячейки,
                // в которой лежит её центр — суммы по строкам/колонкам сходятся
                // с «Всего», а на плане видно раздельно: траншея и зумпф.
                if (_anomalyZones != null)
                {
                    for (int zi = 0; zi < _anomalyZones.Count; zi++)
                    {
                        var z = _anomalyZones[zi];
                        CalcZoneVolume(z, surf1!, surf2!, cosA, sinA,
                            out double zVol, out double zArea,
                            out double zFillArea, out double zCutArea);
                        totalArea     += zArea;
                        totalCutArea  += zCutArea;
                        totalFillArea += zFillArea;
                        DrawZoneRect(trans, z, cosA, sinA);
                        ed.WriteMessage(
                            $"\n[Картограмма] Перепад #{zi + 1}: объём {Signed(zVol, _o.VolumePrecision)} м³, площадь {zArea:F2} м²");

                        if (Math.Abs(zVol) < _o.MinVolume) continue;

                        double zcx = (z.x0 + z.x1) / 2.0, zcy = (z.y0 + z.y1) / 2.0;
                        FindInsideAnchor(z.x0, z.y0, z.x1 - z.x0, z.y1 - z.y0,
                            zcx, zcy, cosA, sinA, surf1!, surf2!, out double zLx, out double zLy);
                        if (_o.ShowVolumeArea)
                            PlaceVolumeAreaLabel(trans, Signed(zVol, _o.VolumePrecision),
                                Area(zArea), _o.VolumeTextHeight, angle, _o.ColorVolume,
                                z.x0, z.y0, z.x1 - z.x0, z.y1 - z.y0, zLx, zLy,
                                PartPredicate(VolLabelKind.Cell, surf1, surf2));
                        else
                        PlaceVolumeLabel(AddCenteredText(trans, _o.VolumeLayerName,
                            LW(zLx, zLy, cosA, sinA),
                            Signed(zVol, _o.VolumePrecision),
                            _o.VolumeTextHeight, angle, _o.ColorVolume,
                            hideMask: _o.HideMaskVolume),
                            z.x0, z.y0, z.x1 - z.x0, z.y1 - z.y0, zLx, zLy,
                            PartPredicate(VolLabelKind.Cell, surf1, surf2), VolLabelKind.Cell);

                        double zDisp = double.Parse(
                            Math.Abs(zVol).ToString("F" + _o.VolumePrecision,
                                System.Globalization.CultureInfo.InvariantCulture),
                            System.Globalization.CultureInfo.InvariantCulture);
                        int zc = Clamp((int)(zcx / _o.CellSizeX), 0, cols - 1);
                        int zr = Clamp((int)(zcy / _o.CellSizeY), 0, rows - 1);
                        if (zVol < 0) { totCut  += zDisp; colCut[zc]  += zDisp; rowCut[zr]  += zDisp; }
                        else          { totFill += zDisp; colFill[zc] += zDisp; rowFill[zr] += zDisp; }
                    }
                }

                if (_o.DrawSummaryTable && dataCells.Count > 0)
                {
                    // Компактная таблица — только активный диапазон строк/столбцов
                    int minR = dataCells.Min(cl => cl.Row);
                    int maxR = dataCells.Max(cl => cl.Row);
                    int minC = dataCells.Min(cl => cl.Col);
                    int maxC = dataCells.Max(cl => cl.Col);

                    int aRows = maxR - minR + 1;
                    int aCols = maxC - minC + 1;

                    var aColCut  = new double[aCols];
                    var aColFill = new double[aCols];
                    var aRowCut  = new double[aRows];
                    var aRowFill = new double[aRows];
                    Array.Copy(colCut,  minC, aColCut,  0, aCols);
                    Array.Copy(colFill, minC, aColFill, 0, aCols);
                    Array.Copy(rowCut,  minR, aRowCut,  0, aRows);
                    Array.Copy(rowFill, minR, aRowFill, 0, aRows);

                    Report("Построение итоговой таблицы…", 92);
                    DrawSummaryTable(trans, aRows, aCols, cosA, sinA,
                        aColCut, aColFill, aRowCut, aRowFill, totCut, totFill,
                        minR, minC, totalArea, totalCutArea, totalFillArea,
                        _o.HatchCut.Enabled, _o.HatchFill.Enabled);
                }

                Report("Фиксация транзакции…", 98);
                trans.Commit();

                ed.WriteMessage($"\n[Картограмма] Объём посчитан (шаг субсетки {StepText()} м).");
                ed.WriteMessage($"\n[Картограмма] Выемка: -{totCut:F2} м³  |  Насыпь: +{totFill:F2} м³\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Удалить сетку и подписи отметок
        // ═══════════════════════════════════════════════════════════════════════
        public void DeleteGrid()
        {
            var ed = _doc.Editor;
            try
            {
                using var trans = _db.TransactionManager.StartTransaction();
                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Кресты ручных троек остаются вместе с тройками.
                int n = EraseByLayer(trans, ms, _o.GridLayerName, e => !IsManualCross(e));
                trans.Commit();

                ed.WriteMessage($"\n[Картограмма] Удалено {n} объектов сетки.\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА удаления: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Удалить подписи объёмов
        // ═══════════════════════════════════════════════════════════════════════
        public void DeleteVolume()
        {
            var ed = _doc.Editor;
            try
            {
                using var trans = _db.TransactionManager.StartTransaction();
                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int n = EraseByLayer(trans, ms, _o.TextLayerName)
                      + EraseByLayer(trans, ms, _o.WorkLayerName)
                      + EraseByLayer(trans, ms, _o.ExistLayerName)
                      + EraseByLayer(trans, ms, _o.DesignLayerName)
                      + EraseByLayer(trans, ms, _o.VolumeLayerName)
                      + EraseByLayer(trans, ms, _o.TableLayerName)
                      // Штриховка выемки/насыпи и линия нулевых работ рисуются
                      // вместе с объёмами (см. CalculateVolume → DrawHatchAndZeroLine)
                      // по тем же ячейкам сетки — при удалении объёмов их тоже
                      // нужно убрать, иначе штриховка остаётся висеть без данных.
                      + EraseByLayer(trans, ms, _o.HatchLayerName)
                      + EraseByLayer(trans, ms, _o.ZeroLineLayerName)
                      // Кресты ручных троек — на слое сетки, уходят с отметками.
                      + EraseByLayer(trans, ms, _o.GridLayerName, IsManualCross);
                trans.Commit();

                ed.WriteMessage($"\n[Картограмма] Удалено {n} объектов (отметки + объёмы + штриховка).\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА удаления: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  По клику на любую из трёх цифр тройки находит точный узел —
        //  пересечение, из которого должна стартовать выноска и где должен
        //  стоять разделительный крестик.
        //  Алгоритм: берём БЛИЖАЙШИЙ к клику MText одного из трёх слоёв — это
        //  и есть метка, которую пользователь имеет в виду. Узел вычисляется из
        //  её позиции по известному смещению её роли (рабочая/проектная/
        //  существующая) — метки рисовались с точными смещениями от узла,
        //  поэтому результат однозначен даже в самом густом скоплении троек
        //  (никакого «поиска в радиусе», где можно зацепить чужую метку).
        // ═══════════════════════════════════════════════════════════════════════
        public Point3d? FindTripletOrigin(Point3d clickedPt)
        {
            try
            {
                using var trans = _db.TransactionManager.StartTransaction();
                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                string[] layers = { _o.WorkLayerName, _o.DesignLayerName, _o.ExistLayerName };

                // Ищем ближайший MText любого из трёх слоёв к клику
                MText? nearestAny = null;
                double nearestDist = double.MaxValue;
                foreach (ObjectId id in ms)
                {
                    var mt = trans.GetObject(id, OpenMode.ForRead) as MText;
                    if (mt == null) continue;
                    if (Array.IndexOf(layers, mt.Layer) < 0) continue;
                    double d = (mt.Location - clickedPt).Length;
                    if (d < nearestDist) { nearestDist = d; nearestAny = mt; }
                }
                if (nearestAny == null) { trans.Commit(); return null; }

                int role = Array.IndexOf(layers, nearestAny.Layer);
                var p    = nearestAny.Location;
                trans.Commit();

                double cA     = Math.Cos(_o.RotationRadians);
                double sA     = Math.Sin(_o.RotationRadians);
                double margin = _o.SmallTextHeight * 0.15;
                double sh     = _o.SmallTextHeight;

                // Обратные смещения «метка → узел» (локальные, поворачиваются):
                //   рабочая:       якорь (nx−margin, ny+margin)      ⇒ +(margin, −margin)
                //   проектная:     якорь (nx+margin, ny+margin)      ⇒ +(−margin, −margin)
                //   существующая:  якорь (nx+margin, ny−margin−sh)   ⇒ +(−margin, margin+sh)
                double lx = role == 0 ? margin : -margin;
                double ly = role == 2 ? margin + sh : -margin;
                return new Point3d(
                    p.X + lx * cA - ly * sA,
                    p.Y + lx * sA + ly * cA,
                    clickedPt.Z);
            }
            catch
            {
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Выноска тройки отметок. Каждая из трёх меток (рабочая / проектная /
        //  существующая) находится по её ТОЧНОЙ ожидаемой позиции относительно
        //  узла origin (метки рисовались с известными смещениями) с допуском в
        //  долю высоты текста — надёжно даже в самом густом скоплении троек.
        //  Все три переносятся на target единым смещением (строй сохраняется),
        //  под тройкой на новом месте рисуется «+»-перекрестье по границам цифр
        //  и линия-выноска к нему из ПЕРВОНАЧАЛЬНОГО узла (см. DrawCalloutMarker).
        //  Если хоть одна метка не найдена — не переносится ничего (тройка
        //  ходит только целиком).
        // ═══════════════════════════════════════════════════════════════════════
        public bool CreateLabelCallout(Point3d origin, Point3d target)
        {
            var ed = _doc.Editor;
            try
            {
                using var trans = _db.TransactionManager.StartTransaction();
                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                EnsureLayer(trans, _o.TableLayerName, _o.ColorTable);

                string[] layers = { _o.WorkLayerName, _o.DesignLayerName, _o.ExistLayerName };

                // Метки тройки рисуются с ТОЧНЫМИ смещениями от узла — вычисляем
                // ожидаемую позицию каждой роли и матчим по ней с малым допуском.
                // Это однозначно выбирает СВОЮ тройку даже в густом скоплении
                // (прежний поиск «ближайшей в радиусе полуячейки» цеплял чужую
                // существующую отметку — она в тройке самая дальняя от узла).
                double margin = _o.SmallTextHeight * 0.15;
                double sh     = _o.SmallTextHeight;
                double cAr    = Math.Cos(_o.RotationRadians);
                double sAr    = Math.Sin(_o.RotationRadians);
                Point3d Expected(double lx, double ly) => new Point3d(
                    origin.X + lx * cAr - ly * sAr,
                    origin.Y + lx * sAr + ly * cAr,
                    origin.Z);
                var expected = new[]
                {
                    Expected(-margin,  margin),        // рабочая
                    Expected( margin,  margin),        // проектная
                    Expected( margin, -margin - sh),   // существующая
                };
                double tol = sh * 0.75;   // допуск — доля высоты текста, не ячейки

                var nearest = new MText?[3];
                var nearestDist = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                foreach (ObjectId id in ms)
                {
                    var mt = trans.GetObject(id, OpenMode.ForRead) as MText;
                    if (mt == null) continue;
                    int idx = Array.IndexOf(layers, mt.Layer);
                    if (idx < 0) continue;
                    double d = (mt.Location - expected[idx]).Length;
                    if (d > tol) continue;
                    if (d < nearestDist[idx])
                    {
                        nearestDist[idx] = d;
                        nearest[idx]     = mt;
                    }
                }

                if (nearest[0] == null || nearest[1] == null || nearest[2] == null)
                {
                    string[] roleNames = { "рабочая", "проектная (красная)", "существующая (чёрная)" };
                    var missing = new List<string>();
                    for (int i = 0; i < 3; i++)
                        if (nearest[i] == null) missing.Add(roleNames[i]);
                    ed.WriteMessage(
                        "\n[Картограмма] Тройка не перенесена — не найдена метка: " +
                        string.Join(", ", missing) + ". Переносим только полную тройку.");
                    trans.Commit();
                    return false;
                }

                var delta = new Vector3d(target.X - origin.X, target.Y - origin.Y, 0);
                var moved = new MText[3];
                for (int i = 0; i < 3; i++)
                {
                    moved[i] = (MText)trans.GetObject(nearest[i]!.ObjectId, OpenMode.ForWrite);
                    moved[i].Location = moved[i].Location + delta;
                }

                // Старое перекрестье в узле origin: «+»-маркер прошлой выноски
                // (слой таблицы) или крест ручной тройки (слой сетки; в чертежах
                // прошлых версий — слой рабочих отметок).
                // И линия-выноска к нему. ПЕРВОНАЧАЛЬНЫЙ узел тройки и handle
                // выноски хранятся в XData самих меток (пишется при каждом
                // переносе), поэтому сколько ни переносить тройку, выноска идёт
                // из одного и того же исходного узла. Поиск выноски по геометрии
                // (её дальний конец — исходный узел) — только для троек,
                // перенесённых прошлыми версиями без XData: он теряет узел, если
                // тройку поставили рядом с исходным местом и выноска не рисовалась,
                // или цепляет чужую линию слоя таблицы у края креста.
                double nodeTol = sh * 0.1;
                var lines    = CollectLines(trans, ms,
                    _o.GridLayerName, _o.WorkLayerName, _o.TableLayerName);
                var crossIds = lines.Where(l => PassesThroughNode(l.S, l.E, origin, nodeTol))
                                    .Select(l => l.Id).ToList();
                var leaderId = FindTripleLeader(moved, lines, crossIds, origin, nodeTol, out var anchor);
                if (!leaderId.IsNull && !crossIds.Contains(leaderId)) crossIds.Add(leaderId);
                foreach (var id in crossIds)
                    trans.GetObject(id, OpenMode.ForWrite).Erase();

                var newLeader = DrawCalloutMarker(trans, ms, target,
                    moved[0], moved[1], moved[2], anchor);
                EnsureRegApp(trans);
                WriteTripleCallout(moved, anchor, newLeader);

                trans.Commit();
                return true;
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА выноски: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Перекрестье тройки и линия-выноска — общие для выноски и перерисовки.
        // ═══════════════════════════════════════════════════════════════════════
        private static List<(ObjectId Id, Point3d S, Point3d E, string Layer)> CollectLines(
            Transaction t, BlockTableRecord ms, params string[] layers)
        {
            var res = new List<(ObjectId Id, Point3d S, Point3d E, string Layer)>();
            foreach (ObjectId id in ms)
            {
                if (t.GetObject(id, OpenMode.ForRead) is not Line ln) continue;
                if (Array.IndexOf(layers, ln.Layer) < 0) continue;
                res.Add((id, ln.StartPoint, ln.EndPoint, ln.Layer));
            }
            return res;
        }

        /// <summary>Отрезок — луч перекрестья узла: идёт по оси сетки, и узел
        /// лежит на нём строго внутри (а не на конце, как у выноски).</summary>
        private bool PassesThroughNode(Point3d s, Point3d e, Point3d node, double tol)
        {
            double dx = e.X - s.X, dy = e.Y - s.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return false;
            double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);
            bool alongX = Math.Abs(dx * sA - dy * cA) / len < 1e-3;
            bool alongY = Math.Abs(dx * cA + dy * sA) / len < 1e-3;
            if (!alongX && !alongY) return false;
            double k = ((node.X - s.X) * dx + (node.Y - s.Y) * dy) / (len * len);
            if (k * len <= tol || (1 - k) * len <= tol) return false;
            double px = s.X + k * dx - node.X, py = s.Y + k * dy - node.Y;
            return Math.Sqrt(px * px + py * py) <= tol;
        }

        /// <summary>Линия-выноска к перекрестью узла: отрезок слоя таблицы, один
        /// конец которого лежит в узле (выноски старых версий шли в центр) или на
        /// конце луча перекрестья. anchor — её дальний конец, первоначальный узел
        /// тройки; если выноски нет — сам узел.</summary>
        private ObjectId FindLeader(List<(ObjectId Id, Point3d S, Point3d E, string Layer)> lines,
            List<ObjectId> crossIds, Point3d node, double tol, out Point3d anchor)
        {
            var ends = new List<Point3d> { node };
            foreach (var l in lines)
                if (crossIds.Contains(l.Id)) { ends.Add(l.S); ends.Add(l.E); }

            static double D(Point3d a, Point3d b) =>
                Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

            anchor = node;
            var best = ObjectId.Null;
            double bestD = double.MaxValue;
            foreach (var l in lines)
            {
                if (l.Layer != _o.TableLayerName || crossIds.Contains(l.Id)) continue;
                foreach (var p in ends)
                {
                    double dS = D(l.S, p), dE = D(l.E, p);
                    double d  = Math.Min(dS, dE);
                    if (d > tol || d >= bestD) continue;
                    bestD  = d;
                    best   = l.Id;
                    anchor = dS <= dE ? l.E : l.S;
                }
            }
            return best;
        }

        /// <summary>
        /// «+»-перекрестье тройки по границам её цифр: горизонталь — от левого
        /// края рабочей отметки до правого края самой широкой из проектной /
        /// существующей, вертикаль — от низа существующей до верха верхних
        /// отметок. Если задан anchor (первоначальный узел вынесенной тройки) —
        /// ещё и линия-выноска из него к краю креста со стороны anchor: к нижнему
        /// концу, если узел под крестом, иначе к левому или правому. Верхний
        /// конец зажат между рабочей и проектной отметками и не используется.
        /// </summary>
        private ObjectId DrawTripleCross(Transaction t, BlockTableRecord ms, Point3d node,
            MText work, MText design, MText exist, string layer, int aci, Point3d? anchor)
        {
            double sh = work.TextHeight, m = sh * 0.15;
            double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);
            double z  = work.Location.Z;

            double xL = -(m + work.ActualWidth);
            double xR =   m + Math.Max(design.ActualWidth, exist.ActualWidth);
            double yB = -(m + sh);
            double yT =   m + sh;

            Point3d P(double lx, double ly) =>
                new Point3d(node.X + lx * cA - ly * sA, node.Y + lx * sA + ly * cA, z);
            ObjectId Add(Point3d a, Point3d b)
            {
                var ln = new Line(a, b)
                {
                    Layer = layer,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, (short)aci)
                };
                var id = ms.AppendEntity(ln);
                t.AddNewlyCreatedDBObject(ln, true);
                return id;
            }

            Add(P(xL, 0), P(xR, 0));
            Add(P(0, yB), P(0, yT));
            if (anchor == null) return ObjectId.Null;

            double ox = anchor.Value.X - node.X, oy = anchor.Value.Y - node.Y;
            double lxo =  ox * cA + oy * sA;   // в осях сетки
            double lyo = -ox * sA + oy * cA;
            bool inX = lxo >= xL && lxo <= xR;
            // Тройку вернули на исходное место — выноска не нужна.
            if (inX && lyo >= yB && lyo <= yT) return ObjectId.Null;

            Point3d attach = lyo < 0 && inX ? P(0, yB)
                           : lxo < 0        ? P(xL, 0)
                           :                  P(xR, 0);
            return Add(new Point3d(anchor.Value.X, anchor.Value.Y, z), attach);
        }

        /// <summary>Крест вынесенной тройки с выноской — слой и цвет таблицы.
        /// Возвращает выноску (Null, если тройка стоит на исходном месте).</summary>
        private ObjectId DrawCalloutMarker(Transaction t, BlockTableRecord ms, Point3d node,
            MText work, MText design, MText exist, Point3d anchor) =>
            DrawTripleCross(t, ms, node, work, design, exist,
                _o.TableLayerName, _o.ColorTable, anchor);

        /// <summary>Первоначальный узел вынесенной тройки и её выноска — из
        /// XData меток (одинаковая у всех трёх; берётся первая найденная).
        /// false — тройка ещё не переносилась или перенесена прошлой версией.</summary>
        private bool TryReadTripleCallout(MText[] labels, out Point3d anchor, out ObjectId leaderId)
        {
            anchor   = Point3d.Origin;
            leaderId = ObjectId.Null;
            foreach (var mt in labels)
            {
                var rb = mt.GetXDataForApplication(XAppName);
                if (rb == null) continue;
                bool hasAnchor = false;
                foreach (TypedValue tv in rb)
                {
                    if (tv.TypeCode == (int)DxfCode.ExtendedDataXCoordinate)
                    {
                        anchor    = (Point3d)tv.Value;
                        hasAnchor = true;
                    }
                    else if (tv.TypeCode == (int)DxfCode.ExtendedDataHandle)
                    {
                        try
                        {
                            var h  = new Handle(Convert.ToInt64((string)tv.Value, 16));
                            var id = _db.GetObjectId(false, h, 0);
                            if (!id.IsNull && !id.IsErased) leaderId = id;
                        }
                        catch { }
                    }
                }
                if (hasAnchor) return true;
                leaderId = ObjectId.Null;
            }
            return false;
        }

        /// <summary>Первоначальный узел и текущая выноска вынесенной тройки.
        /// Узел — из XData меток; без неё (тройка из прошлых версий) — по
        /// геометрии (FindLeader). Выноска — по handle из XData; если её там
        /// нет или она удалена (тройку переносили прошлой версией, которая
        /// XData не обновляет) — отрезок у креста, идущий именно из этого узла.</summary>
        private ObjectId FindTripleLeader(MText[] labels,
            List<(ObjectId Id, Point3d S, Point3d E, string Layer)> lines,
            List<ObjectId> crossIds, Point3d node, double tol, out Point3d anchor)
        {
            if (!TryReadTripleCallout(labels, out anchor, out var leaderId))
                return FindLeader(lines, crossIds, node, tol, out anchor);
            if (!leaderId.IsNull) return leaderId;

            var geo = FindLeader(lines, crossIds, node, tol, out var geoAnchor);
            double dx = geoAnchor.X - anchor.X, dy = geoAnchor.Y - anchor.Y;
            return !geo.IsNull && Math.Sqrt(dx * dx + dy * dy) <= tol ? geo : ObjectId.Null;
        }

        private static void WriteTripleCallout(MText[] labels, Point3d anchor, ObjectId leaderId)
        {
            foreach (var mt in labels)
            {
                if (!mt.IsWriteEnabled) mt.UpgradeOpen();
                WriteVolumeCallout(mt, anchor, leaderId);
            }
        }

        /// <summary>Крест ручной тройки (DrawManualTriple): на слое сетки, цвет —
        /// стандартный белый/чёрный AutoCAD (ACI 7), как узел сетки. Это
        /// единственные отрезки (Line) на слое сетки — сама сетка из полилиний,
        /// поэтому перестроение/удаление сетки их не трогает (IsManualCross), а
        /// удаляются они вместе с отметками.</summary>
        private void DrawManualCross(Transaction t, BlockTableRecord ms, Point3d node,
            MText work, MText design, MText exist) =>
            DrawTripleCross(t, ms, node, work, design, exist, _o.GridLayerName, 7, null);

        private static bool IsManualCross(AcadEntity ent) => ent is Line;

        // ═══════════════════════════════════════════════════════════════════════
        //  Выноска объёма. Подпись объёма ячейки переносится целиком, под ней
        //  рисуется черта-подчёркивание по ширине цифры, а к ближнему концу
        //  черты — линия из ПЕРВОНАЧАЛЬНОГО места подписи (одна полилиния).
        //  Исходное место и handle полилинии хранятся в XData самой подписи:
        //  при повторном переносе старая выноска удаляется, а новая снова идёт
        //  из исходного места — без зигзагов.
        // ═══════════════════════════════════════════════════════════════════════
        public enum CalloutKind { None, Triple, Volume }

        /// <summary>Расстояние от точки до прямоугольника текста в его
        /// собственных осях (0 — точка внутри). Габариты GeometricExtents
        /// для повёрнутого текста — описанный мировой прямоугольник, заметно
        /// больше самих цифр, и соседние подписи в нём перекрываются.</summary>
        private static double DistanceToText(MText mt, Point3d p)
        {
            double a = TextAngle(mt), c = Math.Cos(a), s = Math.Sin(a);
            double dx0 = p.X - mt.Location.X, dy0 = p.Y - mt.Location.Y;
            double u =  dx0 * c + dy0 * s;     // вдоль строки от точки вставки
            double v = -dx0 * s + dy0 * c;
            double w = mt.ActualWidth, h = mt.ActualHeight;
            if (!(w > 0)) w = mt.Contents.Length * mt.TextHeight * 0.7;
            if (!(h > 0)) h = mt.TextHeight;
            (double u0, double v0) = mt.Attachment switch
            {
                AttachmentPoint.BottomRight  => (-w, 0.0),
                AttachmentPoint.MiddleCenter => (-w / 2, -h / 2),
                AttachmentPoint.TopLeft      => (0.0, -h),
                _                            => (0.0, 0.0),
            };
            double du = Math.Max(0, Math.Max(u0 - u, u - (u0 + w)));
            double dv = Math.Max(0, Math.Max(v0 - v, v - (v0 + h)));
            return Math.Sqrt(du * du + dv * dv);
        }

        private const string XAppName = "OPENKARTOGRAMMA";

        /// <summary>
        /// Что пользователь указал для переноса: тройку отметок или подпись
        /// объёма. picked — объект под курсором: если это цифра тройки или
        /// объёма, берётся он. Иначе — ближайшая к клику подпись по её
        /// собственному (повёрнутому) прямоугольнику; если клик внутри подписи
        /// объёма, она важнее тройки под ней (объём лежит сверху).
        /// basePt — откуда тянется резинка: узел тройки или центр подписи.
        /// </summary>
        public CalloutKind ClassifyCalloutPick(Point3d click, out Point3d basePt, out ObjectId volumeId,
            ObjectId picked = default)
        {
            basePt   = click;
            volumeId = ObjectId.Null;
            bool isVolume = false;
            try
            {
                using var trans = _db.TransactionManager.StartTransaction();
                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                string[] layers = { _o.WorkLayerName, _o.DesignLayerName,
                                    _o.ExistLayerName, _o.VolumeLayerName };
                MText? best = null;

                // Часть круга «Площадь и объём» (площадь, круг, черта) —
                // переносится вся группа, берём её главный объект.
                if (!picked.IsNull)
                {
                    var master = VolAreaMaster(trans.GetObject(picked, OpenMode.ForRead));
                    if (!master.IsNull) picked = master;
                }

                if (!picked.IsNull && trans.GetObject(picked, OpenMode.ForRead) is MText pm &&
                    Array.IndexOf(layers, pm.Layer) >= 0)
                {
                    best = pm;
                    // Тройку дальше ищет FindTripletOrigin по точке — даём ему
                    // точку вставки выбранной цифры, а не край, куда попал клик.
                    if (pm.Layer != _o.VolumeLayerName) click = pm.Location;
                }
                else
                {
                    double bestD = double.MaxValue;
                    bool bestInVolume = false;
                    foreach (ObjectId id in ms)
                    {
                        if (trans.GetObject(id, OpenMode.ForRead) is not MText mt) continue;
                        if (Array.IndexOf(layers, mt.Layer) < 0) continue;
                        double d = DistanceToText(mt, click);
                        bool inVolume = d <= 0 && mt.Layer == _o.VolumeLayerName;
                        if (bestInVolume && !inVolume) continue;
                        if ((inVolume && !bestInVolume) || d < bestD)
                        { bestD = d; best = mt; bestInVolume = inVolume; }
                    }
                }
                if (best == null) { trans.Commit(); return CalloutKind.None; }

                isVolume = best.Layer == _o.VolumeLayerName;
                if (isVolume)
                {
                    var master = VolAreaMaster(best);
                    if (!master.IsNull) best = (MText)trans.GetObject(master, OpenMode.ForRead);
                    basePt   = TryGetVolAreaGroup(trans, best, out _, out var circ, out _)
                        ? circ.Center : best.Location;
                    volumeId = best.ObjectId;
                }
                trans.Commit();
            }
            catch
            {
                return CalloutKind.None;
            }
            if (isVolume) return CalloutKind.Volume;

            var node = FindTripletOrigin(click);
            if (node == null) return CalloutKind.None;
            basePt = node.Value;
            return CalloutKind.Triple;
        }

        public bool CreateVolumeCallout(ObjectId labelId, Point3d target)
        {
            var ed = _doc.Editor;
            try
            {
                using var trans = _db.TransactionManager.StartTransaction();
                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureRegApp(trans);

                var mt = (MText)trans.GetObject(labelId, OpenMode.ForWrite);
                ReadVolumeCallout(mt, out var anchor, out var leaderId);
                if (!leaderId.IsNull) trans.GetObject(leaderId, OpenMode.ForWrite).Erase();

                // Круг «Площадь и объём» — переносится целиком: объём,
                // площадь, круг и черта; target — новый центр круга.
                if (TryGetVolAreaGroup(trans, mt, out var area, out var circ, out var div))
                {
                    if (mt.GetXDataForApplication(XAppName) == null) anchor = circ.Center;
                    var c0 = circ.Center;
                    var move = Matrix3d.Displacement(new Point3d(target.X, target.Y, c0.Z) - c0);
                    foreach (var e in new AcadEntity[] { mt, area, circ, div })
                    {
                        if (!e.IsWriteEnabled) e.UpgradeOpen();
                        e.TransformBy(move);
                    }
                    WriteVolumeCallout(mt, anchor, DrawVolumeAreaLeader(trans, ms, circ, div, anchor));
                    trans.Commit();
                    return true;
                }

                mt.Location = new Point3d(target.X, target.Y, mt.Location.Z);
                var plId = DrawVolumeLeader(trans, ms, mt, anchor);
                WriteVolumeCallout(mt, anchor, plId);

                trans.Commit();
                return true;
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА выноски объёма: {ex.Message}");
                return false;
            }
        }

        /// <summary>После смены высоты/стиля текста — выноски объёмов заново
        /// по новой ширине цифр, из того же исходного места.</summary>
        private void RefitVolumeCallouts()
        {
            using var trans = _db.TransactionManager.StartTransaction();
            var rat = (RegAppTable)trans.GetObject(_db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(XAppName)) { trans.Commit(); return; }

            var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)trans.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var ids = new List<ObjectId>();
            foreach (ObjectId id in ms)
                if (trans.GetObject(id, OpenMode.ForRead) is MText mt &&
                    mt.Layer == _o.VolumeLayerName &&
                    mt.GetXDataForApplication(XAppName) != null)
                    ids.Add(id);

            foreach (var id in ids)
            {
                var mt = (MText)trans.GetObject(id, OpenMode.ForWrite);
                ReadVolumeCallout(mt, out var anchor, out var leaderId);
                if (!leaderId.IsNull) trans.GetObject(leaderId, OpenMode.ForWrite).Erase();
                var plId = TryGetVolAreaGroup(trans, mt, out _, out var circ, out var div)
                    ? DrawVolumeAreaLeader(trans, ms, circ, div, anchor)
                    : DrawVolumeLeader(trans, ms, mt, anchor);
                WriteVolumeCallout(mt, anchor, plId);
            }
            trans.Commit();
        }

        private void ReadVolumeCallout(MText mt, out Point3d anchor, out ObjectId leaderId)
        {
            anchor   = mt.Location;   // ещё не переносилась — исходное место здесь
            leaderId = ObjectId.Null;
            var rb = mt.GetXDataForApplication(XAppName);
            if (rb == null) return;
            foreach (TypedValue tv in rb)
            {
                if (tv.TypeCode == (int)DxfCode.ExtendedDataXCoordinate)
                    anchor = (Point3d)tv.Value;
                else if (tv.TypeCode == (int)DxfCode.ExtendedDataHandle)
                {
                    try
                    {
                        var h  = new Handle(Convert.ToInt64((string)tv.Value, 16));
                        var id = _db.GetObjectId(false, h, 0);
                        if (!id.IsNull && !id.IsErased) leaderId = id;
                    }
                    catch { }
                }
            }
        }

        private static void WriteVolumeCallout(MText mt, Point3d anchor, ObjectId leaderId)
        {
            var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, XAppName),
                new TypedValue((int)DxfCode.ExtendedDataXCoordinate, anchor));
            if (!leaderId.IsNull)
                rb.Add(new TypedValue((int)DxfCode.ExtendedDataHandle, leaderId.Handle.ToString()));
            mt.XData = rb;
        }

        /// <summary>
        /// Полилиния выноски объёма: исходное место → ближний к нему конец
        /// черты под цифрой → дальний конец. Черта — по ширине цифры с малым
        /// запасом, чуть ниже текста. Если подпись вернули на исходное место
        /// (оно под самой цифрой) — выноска не нужна, возвращает Null.
        /// </summary>
        private ObjectId DrawVolumeLeader(Transaction t, BlockTableRecord ms, MText mt, Point3d anchor)
        {
            double h    = mt.TextHeight;
            double half = mt.ActualWidth * 0.5 + h * 0.15;
            double uy   = -(Math.Max(mt.ActualHeight, h) * 0.5 + h * 0.2);
            double c = Math.Cos(TextAngle(mt)), s = Math.Sin(TextAngle(mt));
            var o = mt.Location;   // MiddleCenter — центр цифры
            Point2d P(double lx, double ly) =>
                new Point2d(o.X + lx * c - ly * s, o.Y + lx * s + ly * c);

            double ox = anchor.X - o.X, oy = anchor.Y - o.Y;
            double ax =  ox * c + oy * s;
            double ay = -ox * s + oy * c;
            if (Math.Abs(ax) <= half && Math.Abs(ay) <= -uy) return ObjectId.Null;

            var pl = new Polyline();
            pl.SetDatabaseDefaults();
            pl.AddVertexAt(0, new Point2d(anchor.X, anchor.Y), 0, 0, 0);
            pl.AddVertexAt(1, ax < 0 ? P(-half, uy) : P(half, uy), 0, 0, 0);
            pl.AddVertexAt(2, ax < 0 ? P(half, uy) : P(-half, uy), 0, 0, 0);
            pl.Elevation = o.Z;
            pl.Layer = _o.VolumeLayerName;
            pl.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)_o.ColorVolume);
            ms.AppendEntity(pl);
            t.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }

        private void EnsureRegApp(Transaction t)
        {
            var rat = (RegAppTable)t.GetObject(_db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(XAppName)) return;
            rat.UpgradeOpen();
            var rec = new RegAppTableRecord { Name = XAppName };
            rat.Add(rec);
            t.AddNewlyCreatedDBObject(rec, true);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Ручная тройка отметок в произвольной точке чертежа (кнопка-«перекрестье»
        //  в разделе «Отметки»). Рисует ту же тройку (рабочая / проектная / чёрная),
        //  что и в узлах сетки — те же слои, стиль, высота, точность и угол — плюс
        //  крестик-маркер точки (в узлах его роль играют линии сетки).
        //  Возвращает false, если в точке нет отметок обеих поверхностей.
        // ═══════════════════════════════════════════════════════════════════════
        public bool DrawManualTriple(Point3d pt)
        {
            var ed = _doc.Editor;
            try
            {
                var civilDoc = CivilApplication.ActiveDocument;
                using var trans = _db.TransactionManager.StartTransaction();

                if (!FindSurfaces(trans, civilDoc, out var surf1, out var surf2))
                {
                    ed.WriteMessage("\n[Картограмма] Ошибка: поверхности не найдены.");
                    return false;
                }

                double? e1 = GetElevS(surf1!, pt.X, pt.Y);
                double? e2 = GetElevS(surf2!, pt.X, pt.Y);
                if (!e1.HasValue || !e2.HasValue) return false;

                var tst = (TextStyleTable)trans.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
                _tsId = tst.Has(_o.TextStyleName) ? tst[_o.TextStyleName] : _db.Textstyle;

                EnsureLayer(trans, _o.WorkLayerName,   _o.ColorWork);
                EnsureLayer(trans, _o.ExistLayerName,  _o.ColorExisting);
                EnsureLayer(trans, _o.DesignLayerName, _o.ColorDesign);
                EnsureLayer(trans, _o.GridLayerName,   7);   // крест тройки

                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                _ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                double ang = _o.RotationRadians;
                double cA  = Math.Cos(ang), sA = Math.Sin(ang);

                // Локальные координаты точки в системе сетки — обратное к LW
                // преобразование; дальше раскладка текстов идентична DrawNodeLabel.
                double dxw = pt.X - _o.BaseX, dyw = pt.Y - _o.BaseY;
                double nx  =  dxw * cA + dyw * sA;
                double ny  = -dxw * sA + dyw * cA;

                double sh     = _o.SmallTextHeight;
                string fmt    = "F" + _o.TextPrecision;
                double margin = sh * 0.15;
                double work   = e2.Value - e1.Value;

                // Рабочая — справа-налево, выше и левее точки
                var mtWork = AddRightAlignedText(trans, _o.WorkLayerName,
                    LW(nx - margin, ny + margin, cA, sA),
                    Signed(work, _o.TextPrecision), sh, ang, _o.ColorWork, _o.HideMaskText);

                // Проектная — слева-направо, выше и правее точки
                var mtDesign = AddTextToLayer(trans, _o.DesignLayerName,
                    LW(nx + margin, ny + margin, cA, sA),
                    Num(e2.Value, _o.TextPrecision), sh, ang, _o.ColorDesign, hideMask: _o.HideMaskText);

                // Существующая — слева-направо, ниже и правее точки
                var mtExist = AddTextToLayer(trans, _o.ExistLayerName,
                    LW(nx + margin, ny - margin - sh, cA, sA),
                    Num(e1.Value, _o.TextPrecision), sh, ang, _o.ColorExisting, hideMask: _o.HideMaskText);

                // Крест-маркер точки по границам цифр, по углу сетки
                // (в узлах сетки его роль играют линии сетки).
                DrawManualCross(trans, _ms, LW(nx, ny, cA, sA), mtWork, mtDesign, mtExist);

                trans.Commit();
                ed.WriteMessage(
                    $"\n[Картограмма] Тройка: чёрная {Num(e1.Value, _o.TextPrecision)}, " +
                    $"красная {Num(e2.Value, _o.TextPrecision)}, рабочая {Signed(work, _o.TextPrecision)}");
                return true;
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА тройки отметок: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Перерисовка «в существующем» — обновляет визуальные свойства
        //  (цвета, высоты, стили текста) у уже размещённых объектов картограммы
        //  без полной перестройки. Структурные параметры (геометрия, точность,
        //  субсетка, точность объёмов) тут не учитываются — для них нужна
        //  полная перестройка.
        // ═══════════════════════════════════════════════════════════════════════
        public int UpdateAppearance()
        {
            var ed = _doc.Editor;
            int updated = 0;
            try
            {
                using var trans = _db.TransactionManager.StartTransaction();

                // Обновляем цвета слоёв (плюс цвет каждой entity, если задан явно)
                SetLayerColor(trans, _o.WorkLayerName,   _o.ColorWork);
                SetLayerColor(trans, _o.DesignLayerName, _o.ColorDesign);
                SetLayerColor(trans, _o.ExistLayerName,  _o.ColorExisting);
                SetLayerColor(trans, _o.VolumeLayerName, _o.ColorVolume);
                SetLayerColor(trans, _o.TableLayerName,  _o.ColorTable);
                SetLayerColor(trans, _o.TextLayerName,   _o.ColorVolume);

                // Резолвим текстовые стили один раз
                ObjectId textTsId  = ResolveTextStyle(trans, _o.TextStyleName);
                ObjectId tableTsId = ResolveTextStyle(trans, _o.TableTextStyleName);

                var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)trans.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId entId in ms)
                {
                    var ent = trans.GetObject(entId, OpenMode.ForRead)
                        as Autodesk.AutoCAD.DatabaseServices.Entity;
                    if (ent == null) continue;
                    string lay = ent.Layer;

                    // Подписи отметок (Work / Design / Exist) — высота SmallTextHeight,
                    // цвет соответствующего слоя, стиль текста подписей.
                    if (lay == _o.WorkLayerName || lay == _o.DesignLayerName ||
                        lay == _o.ExistLayerName || lay == _o.TextLayerName)
                    {
                        int aci = lay == _o.WorkLayerName   ? _o.ColorWork
                                : lay == _o.DesignLayerName ? _o.ColorDesign
                                : lay == _o.ExistLayerName  ? _o.ColorExisting
                                :                              _o.ColorVolume;
                        if (ent is MText lbl && lay != _o.TextLayerName)
                        {
                            AlignToGrid(lbl);
                            ShiftNodeLabel(lbl, lay, _o.SmallTextHeight);
                        }
                        if (UpdateTextEntity(ent, _o.SmallTextHeight, aci, textTsId))
                            updated++;
                        continue;
                    }

                    // Слой объёмов: только текстовые подписи объёмов в ячейках
                    if (lay == _o.VolumeLayerName)
                    {
                        if (ent is DBText || ent is MText)
                        {
                            if (ent is MText vol) AlignToGrid(vol);
                            if (UpdateTextEntity(ent, _o.VolumeTextHeight, _o.ColorVolume, textTsId))
                                updated++;
                        }
                        else if (ent is Circle || ent is Polyline)
                        {
                            // Круги «Площадь и объём», их черты и выноски
                            ent.UpgradeOpen();
                            ent.ColorIndex = _o.ColorVolume;
                            updated++;
                        }
                        continue;
                    }

                    // Слой таблицы: итоговая таблица и текст под/над ней
                    if (lay == _o.TableLayerName)
                    {
                        if (ent is Autodesk.AutoCAD.DatabaseServices.Table tbl)
                        {
                            if (UpdateTableEntity(tbl, _o.TableTextHeight, _o.ColorTable, tableTsId))
                                updated++;
                            continue;
                        }
                        if (ent is DBText || ent is MText)
                        {
                            if (ent is MText tt) AlignToGrid(tt);
                            if (UpdateTextEntity(ent, _o.TableTextHeight, _o.ColorTable, tableTsId))
                                updated++;
                        }
                    }
                }

                trans.Commit();

                // Кресты троек подгоняем уже по новым размерам цифр — в отдельной
                // транзакции, чтобы ширина MText была пересчитана после смены
                // высоты и стиля.
                RefitTripleMarkers();
                // Подписи объёмов — заново без наездов на тройки под новый шрифт.
                RelayoutVolumeLabels();
                RefitVolumeCallouts();
                RebuildSummaryTables();
                ed.WriteMessage($"\n[Картограмма] Обновлено {updated} объектов (внешний вид).");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА перерисовки: {ex.Message}");
            }
            return updated;
        }

        /// <summary>Повернуть подпись по углу сетки в мировых осях. Чертежи,
        /// рассчитанные при повёрнутой ПСК прошлыми версиями, получили угол
        /// от ПСК (MText.Rotation) — точка вставки у них верная, неверен
        /// только поворот. Атташмент сохраняется, поэтому подпись остаётся
        /// на своём месте у узла.</summary>
        private void AlignToGrid(MText mt)
        {
            var dir = TextDir(_o.RotationRadians);
            if (mt.Direction.IsParallelTo(dir) && mt.Direction.DotProduct(dir) > 0) return;
            if (!mt.IsWriteEnabled) mt.UpgradeOpen();
            mt.Direction = dir;
        }

        private static bool UpdateTextEntity(
            Autodesk.AutoCAD.DatabaseServices.Entity ent,
            double height, int aci, ObjectId textStyleId)
        {
            if (!ent.IsWriteEnabled) ent.UpgradeOpen();
            ent.ColorIndex = aci;
            if (ent is DBText dbt)
            {
                dbt.Height = height;
                if (!textStyleId.IsNull) dbt.TextStyleId = textStyleId;
                return true;
            }
            if (ent is MText mt)
            {
                mt.TextHeight = height;
                if (!textStyleId.IsNull) mt.TextStyleId = textStyleId;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Метки тройки стоят со смещениями от узла, зависящими от высоты текста
        /// (зазор 0.15·h, существующая ещё на h ниже). При смене высоты одна
        /// лишь TextHeight оставила бы точку вставки на старом месте — новая
        /// крупная цифра наезжала бы на узел и соседние метки. Поэтому
        /// пересчитываем точку вставки от того же узла под новую высоту; узел
        /// при этом не меняется, так что вынесенные тройки остаются на месте.
        /// Вызывается ДО смены TextHeight.
        /// </summary>
        private void ShiftNodeLabel(MText mt, string layer, double newH)
        {
            double dH = newH - mt.TextHeight;
            if (Math.Abs(dH) < 1e-9) return;
            bool isWork = layer == _o.WorkLayerName;
            if (mt.Attachment != (isWork ? AttachmentPoint.BottomRight : AttachmentPoint.BottomLeft))
                return;

            double dm = dH * 0.15;
            double lx = isWork ? -dm : dm;
            double ly = layer == _o.ExistLayerName ? -dm - dH : dm;
            double c = Math.Cos(TextAngle(mt)), s = Math.Sin(TextAngle(mt));
            if (!mt.IsWriteEnabled) mt.UpgradeOpen();
            mt.Location = mt.Location + new Vector3d(lx * c - ly * s, lx * s + ly * c, 0);
        }

        /// <summary>
        /// После смены высоты текста подгоняет кресты троек под новые цифры:
        /// и крест выноски (с выноской из того же первоначального узла), и
        /// крест ручной тройки — по границам цифр.
        /// </summary>
        private void RefitTripleMarkers()
        {
            using var trans = _db.TransactionManager.StartTransaction();
            var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)trans.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var lines = CollectLines(trans, ms,
                _o.GridLayerName, _o.WorkLayerName, _o.TableLayerName);
            if (lines.Count == 0) { trans.Commit(); return; }

            var works   = new List<MText>();
            var designs = new List<MText>();
            var exists  = new List<MText>();
            foreach (ObjectId id in ms)
            {
                if (trans.GetObject(id, OpenMode.ForRead) is not MText mt) continue;
                if      (mt.Layer == _o.WorkLayerName)   works.Add(mt);
                else if (mt.Layer == _o.DesignLayerName) designs.Add(mt);
                else if (mt.Layer == _o.ExistLayerName)  exists.Add(mt);
            }

            MText? Nearest(List<MText> list, Point3d p, double tol)
            {
                MText? best = null;
                double bestD = tol;
                foreach (var mt in list)
                {
                    double d = (mt.Location - p).Length;
                    if (d <= bestD) { bestD = d; best = mt; }
                }
                return best;
            }

            foreach (var w in works)
            {
                if (w.Attachment != AttachmentPoint.BottomRight) continue;
                double sh = w.TextHeight, m = sh * 0.15;
                double c = Math.Cos(TextAngle(w)), s = Math.Sin(TextAngle(w));
                Point3d L(double lx, double ly) => new Point3d(
                    w.Location.X + lx * c - ly * s, w.Location.Y + lx * s + ly * c, w.Location.Z);
                var node = L(m, -m);   // рабочая стоит в (−m, +m) от узла
                double tol = sh * 0.1;

                // Крест ручной тройки: слой сетки (или рабочих — прошлые версии)
                var wCross = lines.Where(l => l.Layer != _o.TableLayerName &&
                                              PassesThroughNode(l.S, l.E, node, tol))
                                  .Select(l => l.Id).ToList();
                var tCross = lines.Where(l => l.Layer == _o.TableLayerName &&
                                              PassesThroughNode(l.S, l.E, node, tol))
                                  .Select(l => l.Id).ToList();
                if (wCross.Count == 0 && tCross.Count == 0) continue;

                var d = Nearest(designs, L(2 * m, 0), tol);            // узел + (m, m)
                var e = Nearest(exists,  L(2 * m, -2 * m - sh), tol);  // узел + (m, −m−sh)
                if (d == null || e == null) continue;

                if (wCross.Count > 0)
                {
                    foreach (var id in wCross) trans.GetObject(id, OpenMode.ForWrite).Erase();
                    lines.RemoveAll(l => wCross.Contains(l.Id));
                    DrawManualCross(trans, ms, node, w, d, e);
                }

                if (tCross.Count > 0)
                {
                    var triple   = new[] { w, d, e };
                    var leaderId = FindTripleLeader(triple, lines, tCross, node, tol, out var anchor);
                    var gone = new List<ObjectId>(tCross);
                    if (!leaderId.IsNull && !gone.Contains(leaderId)) gone.Add(leaderId);
                    foreach (var id in gone) trans.GetObject(id, OpenMode.ForWrite).Erase();
                    lines.RemoveAll(l => gone.Contains(l.Id));
                    var newLeader = DrawCalloutMarker(trans, ms, node, w, d, e, anchor);
                    EnsureRegApp(trans);
                    WriteTripleCallout(triple, anchor, newLeader);
                }
            }
            trans.Commit();
        }

        private static bool UpdateTableEntity(
            Autodesk.AutoCAD.DatabaseServices.Table tbl,
            double cellTextHeight, int aci, ObjectId textStyleId)
        {
            tbl.UpgradeOpen();
            tbl.ColorIndex = aci;
            int rows = tbl.Rows.Count, cols = tbl.Columns.Count;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var cell = tbl.Cells[r, c];
                    try { cell.TextHeight = cellTextHeight; } catch { }
                    try { cell.ContentColor = Color.FromColorIndex(ColorMethod.ByAci, (short)aci); } catch { }
                    if (!textStyleId.IsNull)
                        try { cell.TextStyleId = textStyleId; } catch { }
                }
            return true;
        }

        private void SetLayerColor(Transaction t, string layerName, int aci)
        {
            var lt = (LayerTable)t.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName)) return;
            var lr = (LayerTableRecord)t.GetObject(lt[layerName], OpenMode.ForWrite);
            lr.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
        }

        private ObjectId ResolveTextStyle(Transaction t, string name)
        {
            var tst = (TextStyleTable)t.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
            return tst.Has(name) ? tst[name] : ObjectId.Null;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Вспомогательные: поиск поверхностей
        // ═══════════════════════════════════════════════════════════════════════
        private bool FindSurfaces(Transaction trans, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
            out CivilSurface? surf1, out CivilSurface? surf2)
        {
            surf1 = surf2 = null;
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                if (trans.GetObject(id, OpenMode.ForRead) is not CivilSurface s) continue;
                if (s.Name == _o.ExistingSurfaceName) surf1 = s;
                if (s.Name == _o.DesignSurfaceName)   surf2 = s;
            }
            return surf1 != null && surf2 != null;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Загрузить ручные границы (внешнюю и внутренние) в поля _boundaryPts /
        //  _innerPtsList. Вызывается ДО CalcAutoGrid, чтобы габариты и базовая
        //  точка сетки могли строиться по внешней границе, а не только по
        //  поверхностям. В авто-режиме поля остаются null.
        // ═══════════════════════════════════════════════════════════════════════
        private void LoadManualBoundaries(Transaction trans)
        {
            _boundaryPts  = null;
            _innerPtsList = null;
            if (_o.AutoBounds) { UpdateBoundaryBbox(); return; }

            _boundaryPts = GetBoundaryPoints(trans, _o.OuterBoundaryId, _doc.Editor, "наружная граница");

            if (_o.InnerBoundaryIds != null && _o.InnerBoundaryIds.Count > 0)
            {
                _innerPtsList = new List<List<Point2d>>();
                foreach (var innerId in _o.InnerBoundaryIds)
                {
                    var ipts = GetBoundaryPoints(trans, innerId, _doc.Editor, "внутренняя граница");
                    if (ipts != null)
                        _innerPtsList.Add(ipts);
                }
            }

            UpdateBoundaryBbox();
        }

        // Габариты наружной границы — быстрый отсев в IsInClipRegion до дорогого
        // point-in-polygon (граница после аппроксимации дуг может иметь сотни
        // вершин, а проверка зовётся на каждый субузел объёмной интеграции).
        private double _bndMinX, _bndMinY, _bndMaxX, _bndMaxY;

        private void UpdateBoundaryBbox()
        {
            _bndMinX = double.MinValue; _bndMinY = double.MinValue;
            _bndMaxX = double.MaxValue; _bndMaxY = double.MaxValue;
            if (_boundaryPts == null || _boundaryPts.Count == 0) return;

            double mnx = double.MaxValue, mny = double.MaxValue;
            double mxx = double.MinValue, mxy = double.MinValue;
            foreach (var p in _boundaryPts)
            {
                if (p.X < mnx) mnx = p.X; if (p.X > mxx) mxx = p.X;
                if (p.Y < mny) mny = p.Y; if (p.Y > mxy) mxy = p.Y;
            }
            _bndMinX = mnx; _bndMinY = mny; _bndMaxX = mxx; _bndMaxY = mxy;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Автобазовая точка — предварительная установка (будет перезаписана CalcAutoGrid)
        // ═══════════════════════════════════════════════════════════════════════
        private void CalcAutoBasePoint(CivilSurface s1, CivilSurface s2)
        {
            // CalcAutoGrid установит точную базовую точку с учётом угла поворота.
            // Этот метод оставлен как заглушка для обратной совместимости.
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Авторасчёт rows/cols — работает в ЛОКАЛЬНОЙ системе координат сетки
        //  (с учётом угла поворота).
        // ═══════════════════════════════════════════════════════════════════════
        private void CalcAutoGrid(CivilSurface s1, CivilSurface s2, out int rows, out int cols)
        {
            var ed = _doc.Editor;
            double sx   = _o.CellSizeX, sy = _o.CellSizeY;
            double ang  = _o.RotationRadians;
            double cosA = Math.Cos(ang), sinA = Math.Sin(ang);

            // Режим раскладки — автоматически по заданным границам:
            //  • внешняя + внутренние («дырки») — траншея-рамка: точная посадка
            //    исходной 1.1.2 (минимум ячеек, сетка натянута на границу —
            //    яма/зумпф попадает в свою ячейку со своим объёмом);
            //  • только внешняя, либо границы автоматически — котлован: целые
            //    квадраты в середине + узкие краевые обрезки (стиль 1.1.1).
            bool wholeCells = _innerPtsList == null || _innerPtsList.Count == 0;
            ed.WriteMessage(wholeCells
                ? "\n[Картограмма] Раскладка сетки: целые квадраты в середине (котлован)"
                : "\n[Картограмма] Раскладка сетки: точно по границе (траншея с внутренними границами)");

            // Проекция мировых координат в ЛОКАЛЬНЫЕ (ось X = направление сетки)
            double ToLX(double wx, double wy) => wx * cosA + wy * sinA;
            double ToLY(double wx, double wy) => -wx * sinA + wy * cosA;

            // ── Приоритет: ручная внешняя граница ────────────────────────────────
            // Если пользователь очертил внешнюю границу, сетка ОБЯЗАНА покрывать
            // именно её. Автопоиск зоны перекрытия поверхностей для повёрнутой
            // узкой траншеи может «уехать» в сторону (берётся меньшая по
            // axis-aligned-габаритам поверхность, а у повёрнутого прямоугольника
            // AABB большой) — поэтому при наличии границы габариты и базовую точку
            // считаем прямо по её вершинам в локальных координатах.
            if (_boundaryPts != null && _boundaryPts.Count >= 3)
            {
                double gMinLX = double.MaxValue, gMinLY = double.MaxValue;
                double gMaxLX = double.MinValue, gMaxLY = double.MinValue;
                foreach (var p in _boundaryPts)
                {
                    double lx = ToLX(p.X, p.Y), ly = ToLY(p.X, p.Y);
                    if (lx < gMinLX) gMinLX = lx; if (lx > gMaxLX) gMaxLX = lx;
                    if (ly < gMinLY) gMinLY = ly; if (ly > gMaxLY) gMaxLY = ly;
                }

                double gW = gMaxLX - gMinLX, gH = gMaxLY - gMinLY;
                LayoutGridAxis(gMinLX, gMaxLX, sx, wholeCells, out cols, out double gBaseLX);
                LayoutGridAxis(gMinLY, gMaxLY, sy, wholeCells, out rows, out double gBaseLY);

                if (_o.AutoBasePoint)
                    SetBaseFromLocal(gBaseLX, gBaseLY, cosA, sinA);

                ed.WriteMessage(
                    $"\n[Картограмма] Габариты по внешней границе: {gW:F3}×{gH:F3} м → {cols}×{rows} ячеек");
                if (_o.AutoBasePoint)
                    ed.WriteMessage($"\n[Картограмма] Базовая точка (по границе): X={_o.BaseX:F3}, Y={_o.BaseY:F3}");
                return;
            }

            var e1 = s1.GeometricExtents;
            var e2 = s2.GeometricExtents;

            double w1 = e1.MaxPoint.X - e1.MinPoint.X, h1 = e1.MaxPoint.Y - e1.MinPoint.Y;
            double w2 = e2.MaxPoint.X - e2.MinPoint.X, h2 = e2.MaxPoint.Y - e2.MinPoint.Y;
            double area1 = w1 * h1, area2 = w2 * h2;

            // Меньшая поверхность нужна только для фолбэка «нет перекрытия» ниже.
            CivilSurface smaller = area1 <= area2 ? s1 : s2;

            ed.WriteMessage($"\n[Картограмма] {s1.Name}: {w1:F3}×{h1:F3} м");
            ed.WriteMessage($"\n[Картограмма] {s2.Name}: {w2:F3}×{h2:F3} м");

            double minLX = double.MaxValue, minLY = double.MaxValue;
            double maxLX = double.MinValue, maxLY = double.MinValue;

            void UpdateBounds(double wx, double wy)
            {
                double lx = ToLX(wx, wy), ly = ToLY(wx, wy);
                if (lx < minLX) minLX = lx; if (lx > maxLX) maxLX = lx;
                if (ly < minLY) minLY = ly; if (ly > maxLY) maxLY = ly;
            }

            void UpdateBoundsFromExtents(Extents3d ext)
            {
                UpdateBounds(ext.MinPoint.X, ext.MinPoint.Y);
                UpdateBounds(ext.MaxPoint.X, ext.MinPoint.Y);
                UpdateBounds(ext.MinPoint.X, ext.MaxPoint.Y);
                UpdateBounds(ext.MaxPoint.X, ext.MaxPoint.Y);
            }

            // ── Габариты зоны ПЕРЕКРЫТИЯ поверхностей ────────────────────────────
            // Берём вершины КАЖДОЙ TIN-поверхности, которые попадают на другую
            // поверхность (там, где заданы обе отметки), и по ним строим габариты.
            // Зона зависит строго от фактического перекрытия и НЕ зависит от того,
            // какая поверхность «меньше» по axis-aligned габаритам. Это ключевое
            // для повёрнутой узкой траншеи: её AABB большой, и прежняя эвристика
            // «по меньшей поверхности» уводила сетку в сторону от перекрытия.
            if (s1 is TinSurface tin1 && tin1.Vertices.Count > 0)
            {
                ed.WriteMessage($"\n[Картограмма] Анализирую {tin1.Vertices.Count} вершин '{s1.Name}'...");
                foreach (TinSurfaceVertex v in tin1.Vertices)
                    if (GetElevS(s2, v.Location.X, v.Location.Y).HasValue)
                        UpdateBounds(v.Location.X, v.Location.Y);
            }
            if (s2 is TinSurface tin2 && tin2.Vertices.Count > 0)
            {
                ed.WriteMessage($"\n[Картограмма] Анализирую {tin2.Vertices.Count} вершин '{s2.Name}'...");
                foreach (TinSurfaceVertex v in tin2.Vertices)
                    if (GetElevS(s1, v.Location.X, v.Location.Y).HasValue)
                        UpdateBounds(v.Location.X, v.Location.Y);
            }

            // Фолбэк: вершины не дали зоны (нет TIN, либо узкая полоса перекрытия
            // прошла между редкими вершинами) — плотно сэмплируем пересечение
            // axis-aligned габаритов поверхностей.
            if (minLX >= maxLX || minLY >= maxLY)
            {
                double mnX = Math.Max(e1.MinPoint.X, e2.MinPoint.X);
                double mnY = Math.Max(e1.MinPoint.Y, e2.MinPoint.Y);
                double mxX = Math.Min(e1.MaxPoint.X, e2.MaxPoint.X);
                double mxY = Math.Min(e1.MaxPoint.Y, e2.MaxPoint.Y);
                if (mnX < mxX && mnY < mxY)
                {
                    ed.WriteMessage("\n[Картограмма] Вершины не дали зоны — сэмплирую пересечение габаритов...");
                    const int NS = 160;
                    double stX = (mxX - mnX) / NS, stY = (mxY - mnY) / NS;
                    for (int i = 0; i <= NS; i++)
                    for (int j = 0; j <= NS; j++)
                    {
                        double wx = mnX + j * stX, wy = mnY + i * stY;
                        if (GetElevS(s1, wx, wy).HasValue && GetElevS(s2, wx, wy).HasValue)
                            UpdateBounds(wx, wy);
                    }
                }
            }

            if (minLX >= maxLX || minLY >= maxLY)
            {
                ed.WriteMessage($"\n[Картограмма] ВНИМАНИЕ: поверхности не перекрываются! Используется bounding box меньшей.");
                minLX = double.MaxValue; minLY = double.MaxValue;
                maxLX = double.MinValue; maxLY = double.MinValue;
                UpdateBoundsFromExtents(smaller.GeometricExtents);
                cols = Clamp((int)Math.Ceiling((maxLX - minLX) / sx), 1, 500);
                rows = Clamp((int)Math.Ceiling((maxLY - minLY) / sy), 1, 500);
                if (_o.AutoBasePoint) SetBaseFromLocal(minLX, minLY, cosA, sinA);
                return;
            }

            // Зона перекрытия найдена по вершинам поверхностей — она очерчивает
            // фактические данные. Раскладку ячеек внутри зоны делает LayoutGridAxis
            // (целые квадраты в середине + симметричные краевые обрезки).
            double realW = maxLX - minLX;
            double realH = maxLY - minLY;
            ed.WriteMessage($"\n[Картограмма] Зона (локальная): {realW:F3}×{realH:F3} м");

            LayoutGridAxis(minLX, maxLX, sx, wholeCells, out cols, out double baseLX);
            LayoutGridAxis(minLY, maxLY, sy, wholeCells, out rows, out double baseLY);

            if (_o.AutoBasePoint)
            {
                SetBaseFromLocal(baseLX, baseLY, cosA, sinA);
                ed.WriteMessage($"\n[Картограмма] Базовая точка (авто, угол {_o.RotationDegrees:F4}°): X={_o.BaseX:F3}, Y={_o.BaseY:F3}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Раскладка сетки по одной оси: сколько ячеек и где начало сетки.
        //  Режим выбирается автоматически по заданным границам (CalcAutoGrid):
        //
        //  wholeCells = false (внешняя + внутренние границы, траншея-рамка) —
        //    ТОЧНАЯ посадка исходной 1.1.2: минимум ячеек ceil(W/шаг), сетка
        //    натянута на границу, излишек поровну с двух сторон — яма/зумпф
        //    попадает в свою ячейку со своим объёмом.
        //
        //  wholeCells = true (только внешняя граница или авто) — раскладка 1.1.1
        //    для котлованов: n целых ячеек в середине зоны + два симметричных
        //    краевых обрезка; внутренность замощена целыми квадратами.
        //
        //  Общее в обоих режимах:
        //    • зона у́же одной ячейки (узкая траншея) → 1 ячейка, зона по центру;
        //    • ширина зоны кратна шагу → ровно n ячеек, сетка совпадает с границей.
        // ═══════════════════════════════════════════════════════════════════════
        internal static void LayoutGridAxis(double zoneMin, double zoneMax, double step,
            bool wholeCells, out int count, out double gridMin)
        {
            double w = Math.Max(zoneMax - zoneMin, 0.0);
            const double tol = 1e-3;                    // 1 мм: считаем «кратно шагу»
            int n = (int)Math.Floor(w / step + 1e-9);
            double rem = w - n * step;

            if      (n <= 0)     count = 1;                         // у́же одной ячейки
            else if (rem <= tol) count = n;                         // точная посадка
            else                 count = wholeCells ? n + 2 : n + 1; // 1.1.1 / 1.1.2
            count = Clamp(count, 1, 500);

            gridMin = zoneMin - (count * step - w) / 2.0;   // излишек поровну с двух сторон
        }

        // Перевод локальной точки (в системе с мировым origin) в мировую базовую точку сетки.
        private void SetBaseFromLocal(double localX, double localY, double cosA, double sinA)
        {
            _o.BaseX = localX * cosA - localY * sinA;
            _o.BaseY = localX * sinA + localY * cosA;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Построить список ячеек с данными (центры ячеек)
        // ═══════════════════════════════════════════════════════════════════════
        private List<CellData> BuildCells(CivilSurface s1, CivilSurface s2,
            int rows, int cols, double cosA, double sinA, double area)
        {
            var cells = new List<CellData>(rows * cols);
            double szX = _o.CellSizeX, szY = _o.CellSizeY;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    // Проверка границы: пропускаем только полностью внешние
                    // (наружная граница) и полностью внутренние («дырки»).
                    // Partial ячейки всегда включаются. В обоих режимах
                    // (обрезать/не обрезать) фильтрация одинакова — в «не
                    // обрезать» ячейки за границей не должны появляться вовсе;
                    // частичные ячейки рисуются целиком (обрезку делает
                    // IsInBounds только в режиме «обрезать»).
                    if (_boundaryPts != null || _innerPtsList != null)
                    {
                        double x0 = c * szX, y0 = r * szY;
                        var corners = new Point2d[4];
                        corners[0] = ToUcs2d(LW(x0,       y0,       cosA, sinA));
                        corners[1] = ToUcs2d(LW(x0 + szX, y0,       cosA, sinA));
                        corners[2] = ToUcs2d(LW(x0 + szX, y0 + szY, cosA, sinA));
                        corners[3] = ToUcs2d(LW(x0,       y0 + szY, cosA, sinA));

                        if (_boundaryPts != null)
                        {
                            if (ClassifyCell(corners, _boundaryPts) == CellClass.Outside)
                            {
                                cells.Add(new CellData { Row = r, Col = c });
                                continue;
                            }
                        }
                        if (_innerPtsList != null)
                        {
                            bool skipCell = false;
                            foreach (var ipts in _innerPtsList)
                            {
                                if (ClassifyCell(corners, ipts) == CellClass.Inside)
                                { skipCell = true; break; }
                            }
                            if (skipCell)
                            {
                                cells.Add(new CellData { Row = r, Col = c });
                                continue;
                            }
                        }
                    }

                    // Сначала пробуем центр ячейки
                    double lx = (c + 0.5) * szX;
                    double ly = (r + 0.5) * szY;
                    double wx = _o.BaseX + lx * cosA - ly * sinA;
                    double wy = _o.BaseY + lx * sinA + ly * cosA;

                    double? e1v = GetElevS(s1, wx, wy);
                    double? e2v = GetElevS(s2, wx, wy);

                    // Для граничных ячеек: центр может быть вне поверхности,
                    // но ячейка всё равно имеет объём. Перебираем те же 8 точек
                    // что использует CellHasOverlap (3×3 без центра).
                    if (!e1v.HasValue || !e2v.HasValue)
                    {
                        for (int fi = 0; fi <= 2 && (!e1v.HasValue || !e2v.HasValue); fi++)
                        for (int fj = 0; fj <= 2 && (!e1v.HasValue || !e2v.HasValue); fj++)
                        {
                            if (fi == 1 && fj == 1) continue; // центр уже проверен
                            double flx = c * szX + fj * szX * 0.5;
                            double fly = r * szY + fi * szY * 0.5;
                            double fwx = _o.BaseX + flx * cosA - fly * sinA;
                            double fwy = _o.BaseY + flx * sinA + fly * cosA;
                            double? fe1 = GetElevS(s1, fwx, fwy);
                            double? fe2 = GetElevS(s2, fwx, fwy);
                            if (fe1.HasValue && fe2.HasValue) { e1v = fe1; e2v = fe2; }
                        }
                    }

                    // Плотный досэмплинг для узких зон перекрытия (тонкая траншея):
                    // грубая 3×3 выборка выше могла не попасть в узкую полосу, и
                    // ячейка осталась бы без данных → без объёма. Включаем при
                    // ручных границах, а в авто-режиме — если сетка не гигантская
                    // (защита от лишней нагрузки; порог как в DrawGridLines).
                    bool allowDense = (_boundaryPts != null || _innerPtsList != null)
                                      || (long)rows * cols <= 20000;
                    if (allowDense && (!e1v.HasValue || !e2v.HasValue))
                    {
                        int ds = DenseOverlapSteps();
                        for (int fi = 0; fi <= ds && (!e1v.HasValue || !e2v.HasValue); fi++)
                        for (int fj = 0; fj <= ds && (!e1v.HasValue || !e2v.HasValue); fj++)
                        {
                            double flx = c * szX + fj * szX / ds;
                            double fly = r * szY + fi * szY / ds;
                            double fwx = _o.BaseX + flx * cosA - fly * sinA;
                            double fwy = _o.BaseY + flx * sinA + fly * cosA;
                            double? fe1 = GetElevS(s1, fwx, fwy);
                            double? fe2 = GetElevS(s2, fwx, fwy);
                            if (fe1.HasValue && fe2.HasValue) { e1v = fe1; e2v = fe2; }
                        }
                    }

                    var cell = new CellData { Row = r, Col = c };
                    if (e1v.HasValue && e2v.HasValue)
                    {
                        cell.HasData    = true;
                        cell.ExistElev  = e1v.Value;
                        cell.DesignElev = e2v.Value;
                        cell.WorkHeight = e2v.Value - e1v.Value;
                        cell.Volume     = cell.WorkHeight * area;
                    }
                    cells.Add(cell);
                }
            }
            return cells;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  ТОЧНЫЙ расчёт объёма ячейки с учётом произвольной границы поверхности.
        //  Для субтреугольников на границе (часть вершин — NaN) выполняется
        //  бинарный поиск точки пересечения границы и считается частичный объём.
        // ═══════════════════════════════════════════════════════════════════════
        /// <summary>Предел узлов субсетки на сторону ячейки: 400² узлов —
        /// ещё быстро. Шаг мельче — точный расчёт по треугольникам TIN.</summary>
        private const int MaxSubgridN = 400;
        private int _exactVolCells, _cappedVolCells;

        /// <summary>Сколько узлов на сторону ячейки просит шаг субсетки.</summary>
        private long SubgridWanted() => Math.Max(4L, (long)Math.Min(1e15, Math.Ceiling(
            Math.Max(_o.CellSizeX, _o.CellSizeY) / Math.Max(_o.VolumeNodeStep, 1e-9))));

        private string StepText() => _o.VolumeNodeStep.ToString("0.######",
            System.Globalization.CultureInfo.InvariantCulture);

        private double CalcCellVolumeAccurate(int r, int c,
            CivilSurface s1, CivilSurface s2, double cosA, double sinA, out double cellArea,
            out double volFill, out double volCut,
            out double areaFill, out double areaCut)
        {
            double szX = _o.CellSizeX;
            double szY = _o.CellSizeY;

            // Шаг мельче, чем позволяет субсетка (вплоть до микрона): объём —
            // точно по треугольникам обеих TIN, это предел субсетки при шаге,
            // стремящемся к нулю. Где точный путь неприменим — субсетка с
            // ограниченным числом узлов.
            long nWant = SubgridWanted();
            if (nWant > MaxSubgridN)
            {
                var ex = CellTouchesAnomalyZone(c * szX, r * szY, szX, szY)
                    ? null : ExactCellSplit(c * szX, r * szY, cosA, sinA);
                if (ex != null)
                {
                    System.Threading.Interlocked.Increment(ref _exactVolCells);
                    cellArea = ex.Area;
                    volFill  = ex.FillVolume;  volCut  = ex.CutVolume;
                    areaFill = ex.FillArea;    areaCut = ex.CutArea;
                    return volFill + volCut;
                }
                System.Threading.Interlocked.Increment(ref _cappedVolCells);
            }

            int    n  = (int)Math.Max(4, Math.Min(nWant, MaxSubgridN));
            double dx = szX / n;
            double dy = szY / n;

            // Предвычисляем мировые координаты и высоты всех узлов сетки
            var h  = new double[n + 1, n + 1];
            var wx = new double[n + 1, n + 1];
            var wy = new double[n + 1, n + 1];

            for (int si = 0; si <= n; si++)
            for (int sj = 0; sj <= n; sj++)
            {
                double lx = c * szX + sj * dx;
                double ly = r * szY + si * dy;
                wx[si, sj] = _o.BaseX + lx * cosA - ly * sinA;
                wy[si, sj] = _o.BaseY + lx * sinA + ly * cosA;

                double? e1 = GetElevS(s1, wx[si, sj], wy[si, sj]);
                double? e2 = GetElevS(s2, wx[si, sj], wy[si, sj]);

                // Точки вне допустимой области помечаются как NaN — CalcSubTriVol
                // обрезает субтреугольники на краю через бинарный поиск
                // (FindBoundaryPoint). IsInBounds сам учитывает ClipCells:
                // «обрезать» → клипать наружную границу и «дырки»;
                // «не обрезать» → клипать только «дырки», наружная игнорируется.
                // Зоны резких перепадов исключаются — их объём считается отдельно.
                if (e1.HasValue && e2.HasValue && IsInBounds(wx[si, sj], wy[si, sj])
                    && !InAnomalyZoneW(wx[si, sj], wy[si, sj]))
                    h[si, sj] = e2.Value - e1.Value;
                else
                    h[si, sj] = double.NaN;
            }

            double vol = 0.0;
            cellArea   = 0.0;
            volFill    = 0.0;
            volCut     = 0.0;
            areaFill   = 0.0;
            areaCut    = 0.0;

            for (int si = 0; si < n; si++)
            for (int sj = 0; sj < n; sj++)
            {
                // Нижний левый треугольник: BL, BR, TL
                vol += CalcSubTriVol(s1, s2,
                    wx[si,   sj  ], wy[si,   sj  ], h[si,   sj  ],
                    wx[si,   sj+1], wy[si,   sj+1], h[si,   sj+1],
                    wx[si+1, sj  ], wy[si+1, sj  ], h[si+1, sj  ],
                    out double aBL, out double fBL, out double cBL,
                    out double afBL, out double acBL);
                cellArea += aBL;
                volFill  += fBL;
                volCut   += cBL;
                areaFill += afBL;
                areaCut  += acBL;

                // Верхний правый треугольник: BR, TR, TL
                vol += CalcSubTriVol(s1, s2,
                    wx[si,   sj+1], wy[si,   sj+1], h[si,   sj+1],
                    wx[si+1, sj+1], wy[si+1, sj+1], h[si+1, sj+1],
                    wx[si+1, sj  ], wy[si+1, sj  ], h[si+1, sj  ],
                    out double aTR, out double fTR, out double cTR,
                    out double afTR, out double acTR);
                cellArea += aTR;
                volFill  += fTR;
                volCut   += cTR;
                areaFill += afTR;
                areaCut  += acTR;
            }

            return vol;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Метод «квадратов»: V = Sэфф × (h1+h2+h3+h4)/4.
        //  Отметки берутся в 4 узлах ячейки. Недостающие (поверхность не покрывает
        //  узел) исключаются из среднего. Эффективная площадь — полная для ячеек
        //  без обрезки либо доля, попавшая в клип-область (оценка 20×20 сэмплов).
        // ═══════════════════════════════════════════════════════════════════════
        private double CalcCellVolumeSquares(int r, int c,
            CivilSurface s1, CivilSurface s2, double cosA, double sinA,
            out double volFill, out double volCut,
            out double areaFill, out double areaCut)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            volFill  = 0.0;
            volCut   = 0.0;
            areaFill = 0.0;
            areaCut  = 0.0;

            // Рабочие отметки в 4 углах ячейки. Порядок обхода (00 → 01 → 11 → 10)
            // нужен для разделения по нулевой линии — оно идёт по контуру клетки.
            var hc = new double[4];
            int[] cornerDx = { 0, 1, 1, 0 };
            int[] cornerDy = { 0, 0, 1, 1 };
            double sum = 0.0;
            int    cnt = 0;
            for (int k = 0; k < 4; k++)
            {
                double lx = (c + cornerDx[k]) * szX;
                double ly = (r + cornerDy[k]) * szY;
                double wx = _o.BaseX + lx * cosA - ly * sinA;
                double wy = _o.BaseY + lx * sinA + ly * cosA;
                double? e1 = GetElevS(s1, wx, wy);
                double? e2 = GetElevS(s2, wx, wy);
                if (!e1.HasValue || !e2.HasValue || InAnomalyZoneW(wx, wy))
                {
                    hc[k] = double.NaN;
                    continue;
                }
                hc[k] = e2.Value - e1.Value;
                sum += hc[k];
                cnt++;
            }
            if (cnt == 0) return 0.0;
            double hAvg = sum / cnt;

            // Эффективная площадь с учётом обрезки
            double effArea;
            if (!_o.ClipCells || (_boundaryPts == null && _innerPtsList == null))
            {
                effArea = szX * szY;
            }
            else
            {
                const int N = 20;
                int inside = 0, total = N * N;
                for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    double lx = c * szX + (j + 0.5) * szX / N;
                    double ly = r * szY + (i + 0.5) * szY / N;
                    double wx = _o.BaseX + lx * cosA - ly * sinA;
                    double wy = _o.BaseY + lx * sinA + ly * cosA;
                    if (IsInBounds(wx, wy)) inside++;
                }
                effArea = szX * szY * (double)inside / total;
            }

            double vol = effArea * hAvg;

            // ── Нулевая линия внутри клетки ───────────────────────────────────
            // Классический метод квадратов усредняет отметки по всей клетке, и
            // для клеток, пересечённых нулевой линией, это неверно: насыпь и
            // выемка взаимно гасятся. Такие клетки разделяем — так же, как это
            // делается вручную: нулевая линия интерполируется по сторонам
            // клетки, каждая часть получает свой объём.
            // Разделяем только когда известны ВСЕ четыре угла: при неполных
            // данных геометрия нулевой линии не определена, оставляем сальдо.
            if (cnt == 4 && ZeroLineSplit.IsMixed(hc))
            {
                ZeroLineSplit.Quad(effArea, hc[0], hc[1], hc[2], hc[3],
                    out volFill, out volCut, out areaFill, out areaCut);
                return volFill + volCut;
            }

            // Однородная клетка — сальдо не меняется, оно же и есть объём
            // своего знака.
            if (vol >= 0) { volFill = vol; areaFill = effArea; }
            else          { volCut  = vol; areaCut  = effArea; }
            return vol;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Эффективная площадь ячейки с учётом перекрытия поверхностей и границ.
        //  Используется для точного подсчёта общей площади картограммы.
        // ═══════════════════════════════════════════════════════════════════════
        private double CalcCellEffectiveArea(int r, int c,
            CivilSurface s1, CivilSurface s2, double cosA, double sinA)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;

            // Сэмплирование 20×20: точка считается «внутри», если обе
            // поверхности возвращают отметку И точка в клип-области.
            const int N = 20;
            int inside = 0, total = N * N;

            for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                double lx = c * szX + (j + 0.5) * szX / N;
                double ly = r * szY + (i + 0.5) * szY / N;
                double wx = _o.BaseX + lx * cosA - ly * sinA;
                double wy = _o.BaseY + lx * sinA + ly * cosA;

                if (!GetElevS(s1, wx, wy).HasValue) continue;
                if (!GetElevS(s2, wx, wy).HasValue) continue;
                if (!IsInClipRegion(wx, wy)) continue;
                if (InAnomalyZoneW(wx, wy)) continue;   // объём зоны — отдельно

                inside++;
            }

            return szX * szY * (double)inside / total;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Объём одного субтреугольника с учётом граничных случаев (NaN вершин).
        //  Когда 1 или 2 вершины вне поверхности — бинарным поиском находится
        //  точка пересечения границы и считается объём частичного треугольника.
        // ═══════════════════════════════════════════════════════════════════════
        private double CalcSubTriVol(CivilSurface s1, CivilSurface s2,
            double xA, double yA, double hA,
            double xB, double yB, double hB,
            double xC, double yC, double hC,
            out double area, out double volFill, out double volCut,
            out double areaFill, out double areaCut)
        {
            area = 0.0;
            volFill = 0.0;
            volCut  = 0.0;
            areaFill = 0.0;
            areaCut  = 0.0;
            bool aOk = !double.IsNaN(hA);
            bool bOk = !double.IsNaN(hB);
            bool cOk = !double.IsNaN(hC);
            int  valid = (aOk ? 1 : 0) + (bOk ? 1 : 0) + (cOk ? 1 : 0);

            if (valid == 0) return 0.0;

            double fullArea = TriArea2D(xA, yA, xB, yB, xC, yC);
            if (fullArea < 1e-14) return 0.0;

            if (valid == 3)
            {
                area = fullArea;
                // Разделение по нулевой линии — только для подписи и итогов;
                // возвращаемое сальдо считается прежней формулой без изменений.
                ZeroLineSplit.Triangle(fullArea, hA, hB, hC,
                    out volFill, out volCut, out areaFill, out areaCut);
                return fullArea * (hA + hB + hC) / 3.0;
            }

            if (valid == 2)
            {
                // Одна вершина вне поверхности.
                // Переопределяем: xOut — вне, xV1/xV2 — внутри.
                double xOut, yOut, xV1, yV1, hV1, xV2, yV2, hV2;
                if (!aOk) { xOut=xA; yOut=yA; xV1=xB; yV1=yB; hV1=hB; xV2=xC; yV2=yC; hV2=hC; }
                else if (!bOk) { xOut=xB; yOut=yB; xV1=xA; yV1=yA; hV1=hA; xV2=xC; yV2=yC; hV2=hC; }
                else           { xOut=xC; yOut=yC; xV1=xA; yV1=yA; hV1=hA; xV2=xB; yV2=yB; hV2=hB; }

                // P — граничная точка на ребре V1→Out
                var (pX, pY, hP) = FindBoundaryPoint(s1, s2, xV1, yV1, xOut, yOut);
                // Q — граничная точка на ребре V2→Out
                var (qX, qY, hQ) = FindBoundaryPoint(s1, s2, xV2, yV2, xOut, yOut);

                // Валидная область: четырёхугольник P-V1-V2-Q
                // Делим на два треугольника: P-V1-V2 и P-V2-Q
                double a1 = TriArea2D(pX, pY, xV1, yV1, xV2, yV2);
                double a2 = TriArea2D(pX, pY, xV2, yV2, qX, qY);
                area = a1 + a2;

                ZeroLineSplit.Triangle(a1, hP, hV1, hV2,
                    out double f1, out double c1, out double af1, out double ac1);
                ZeroLineSplit.Triangle(a2, hP, hV2, hQ,
                    out double f2, out double c2, out double af2, out double ac2);
                volFill  = f1 + f2;
                volCut   = c1 + c2;
                areaFill = af1 + af2;
                areaCut  = ac1 + ac2;

                return a1 * (hP + hV1 + hV2) / 3.0
                     + a2 * (hP + hV2 + hQ) / 3.0;
            }

            if (valid == 1)
            {
                // Две вершины вне поверхности. Одна валидная.
                double xV, yV, hV, xO1, yO1, xO2, yO2;
                if      (aOk) { xV=xA; yV=yA; hV=hA; xO1=xB; yO1=yB; xO2=xC; yO2=yC; }
                else if (bOk) { xV=xB; yV=yB; hV=hB; xO1=xA; yO1=yA; xO2=xC; yO2=yC; }
                else          { xV=xC; yV=yC; hV=hC; xO1=xA; yO1=yA; xO2=xB; yO2=yB; }

                // P — граничная точка на ребре V→O1
                var (pX, pY, hP) = FindBoundaryPoint(s1, s2, xV, yV, xO1, yO1);
                // Q — граничная точка на ребре V→O2
                var (qX, qY, hQ) = FindBoundaryPoint(s1, s2, xV, yV, xO2, yO2);

                // Валидная область: треугольник V-P-Q
                area = TriArea2D(xV, yV, pX, pY, qX, qY);
                ZeroLineSplit.Triangle(area, hV, hP, hQ,
                    out volFill, out volCut, out areaFill, out areaCut);
                return area * (hV + hP + hQ) / 3.0;
            }

            return 0.0;
        }

        // Площадь треугольника по координатам вершин (2D, через векторное произведение)
        private static double TriArea2D(double x1, double y1,
                                         double x2, double y2,
                                         double x3, double y3)
            => Math.Abs((x2 - x1) * (y3 - y1) - (x3 - x1) * (y2 - y1)) * 0.5;

        // ═══════════════════════════════════════════════════════════════════════
        //  Бинарный поиск точки пересечения границы поверхности на отрезке
        //  wxValid→wxInvalid. Возвращает последнюю валидную точку (≈ граница).
        //  steps=6 → точность ~dist/64 (обычно < 1 мм для шага 5 см)
        // ═══════════════════════════════════════════════════════════════════════
        private (double wx, double wy, double h) FindBoundaryPoint(
            CivilSurface s1, CivilSurface s2,
            double wxValid,   double wyValid,
            double wxInvalid, double wyInvalid,
            int steps = 6)
        {
            double loX = wxValid,   loY = wyValid;
            double hiX = wxInvalid, hiY = wyInvalid;

            for (int k = 0; k < steps; k++)
            {
                double midX = (loX + hiX) * 0.5;
                double midY = (loY + hiY) * 0.5;
                double? e1  = GetElevS(s1, midX, midY);
                double? e2  = GetElevS(s2, midX, midY);
                // Точка считается валидной только если обе поверхности имеют
                // данные И точка внутри полигонных границ И вне зон перепадов.
                if (e1.HasValue && e2.HasValue && IsInBounds(midX, midY)
                    && !InAnomalyZoneW(midX, midY))
                    { loX = midX; loY = midY; }
                else
                    { hiX = midX; hiY = midY; }
            }

            // Берём последнюю валидную точку как граничную
            double? h1 = GetElevS(s1, loX, loY);
            double? h2 = GetElevS(s2, loX, loY);
            double hBnd = (h1.HasValue && h2.HasValue && IsInBounds(loX, loY)
                           && !InAnomalyZoneW(loX, loY))
                ? h2.Value - h1.Value : 0.0;
            return (loX, loY, hBnd);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Проверка: есть ли перекрытие обеих поверхностей в данной ячейке.
        //  Проверяем сетку 3×3 точек (центр, углы, середины рёбер) —
        //  если хотя бы одна точка имеет данные на ОБЕИХ поверхностях, ячейка валидна.
        // ═══════════════════════════════════════════════════════════════════════
        // steps — число интервалов по стороне ячейки (steps+1 узлов). По умолчанию
        // 2 → грубая сетка 3×3 (углы + середины рёбер + центр). Для узких зон
        // перекрытия (тонкие траншеи/бермы) вызывается с бо́льшим steps.
        private bool CellHasOverlap(int r, int c,
            CivilSurface s1, CivilSurface s2, double cosA, double sinA, int steps = 2)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;

            for (int i = 0; i <= steps; i++)
            for (int j = 0; j <= steps; j++)
            {
                double lx = c * szX + j * szX / steps;
                double ly = r * szY + i * szY / steps;
                double wx = _o.BaseX + lx * cosA - ly * sinA;
                double wy = _o.BaseY + lx * sinA + ly * cosA;

                if (GetElevS(s1, wx, wy).HasValue && GetElevS(s2, wx, wy).HasValue)
                    return true;
            }

            return false;
        }

        // Плотность досэмплинга для узких зон перекрытия. Узкая полоса (например
        // траншея шириной 0.8 м) может пройти между узлами грубой 3×3 выборки
        // в крупной ячейке (10×10 м) — тогда ячейка ошибочно считается «нет
        // перекрытия» и сетка не строится. Шаг привязан к субсетке объёма
        // (VolumeNodeStep), но ограничен сверху ради производительности.
        private int DenseOverlapSteps()
        {
            int n = (int)Math.Ceiling(
                Math.Max(_o.CellSizeX, _o.CellSizeY) / Math.Max(_o.VolumeNodeStep, 1e-6));
            return Clamp(n, 4, 40);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Рисование сетки: каждая ячейка — отдельный замкнутый прямоугольник.
        //  Рисуются только ячейки, где обе поверхности перекрываются.
        // ═══════════════════════════════════════════════════════════════════════
        private int DrawGridLines(Transaction t, int rows, int cols, double cA, double sA,
            CivilSurface s1, CivilSurface s2)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            int drawn = 0;
            // Ячейки, которым требовалась обрезка по границе, но геометрическая
            // операция не удалась (см. BuildClippedCellRegion) — такие ячейки
            // просто пропускаются молча; считаем их, чтобы предупредить в конце.
            int clipFailed = 0;

            // Загрузить внешнюю границу и внутренние «дырки». Все строятся как
            // свежие плоские полилинии в плоскости Z=0 c Normal=(0,0,1) —
            // обязательно для Region.BooleanOperation, иначе eNonCoplanarGeometry.
            Polyline? outerProto = null;
            List<Point2d>? outerPts = null;
            if (!_o.AutoBounds)
            {
                outerPts = GetBoundaryPoints(t, _o.OuterBoundaryId, _doc.Editor, "наружная граница");
                if (outerPts != null)
                    outerProto = BuildFlatPolyline(outerPts);
            }

            var innerProtos = new List<Polyline>();
            var innerPtsList = new List<List<Point2d>>();
            if (!_o.AutoBounds && _o.InnerBoundaryIds != null)
            {
                foreach (var innerId in _o.InnerBoundaryIds)
                {
                    var ipts = GetBoundaryPoints(t, innerId, _doc.Editor, "внутренняя граница");
                    if (ipts != null)
                    {
                        innerProtos.Add(BuildFlatPolyline(ipts));
                        innerPtsList.Add(ipts);
                    }
                }
            }

            bool hasManualBounds = (outerProto != null && outerPts != null) || innerProtos.Count > 0;

            // Обрезаем по АВТОМАТИЧЕСКОЙ границе (фактическому краю зоны данных,
            // где заданы обе поверхности) во всех случаях, когда нет пригодной
            // ручной границы — не только когда включён флаг «Границы
            // автоматически». Иначе при снятом флаге, но без выбранных внешней
            // и внутренней границ, обрезать было попросту нечем, и «Обрезать
            // квадраты» переставал что-либо делать. Это тот же критерий, по
            // которому ячейка вообще попадает в сетку и считается её объём,
            // поэтому обрезка ничего не меняет в расчётах — только в отрисовке.
            bool autoClip = _o.ClipCells && !hasManualBounds;

            // Плотный досэмплинг тонких зон перекрытия (узкая траншея, которую
            // грубая 3×3 выборка пропускает). Включаем всегда при ручных границах,
            // а в авто-режиме — если сетка не гигантская (защита от лишней нагрузки
            // на очень больших авто-сетках). Порог с запасом покрывает реальные
            // картограммы (до ~140×140 ячеек).
            bool allowDense = hasManualBounds || (long)rows * cols <= 20000;

            for (int r = 0; r < rows; r++)
            {
                // Обрезка по авто-границе семплирует поверхности в краевых
                // ячейках — на крупных сетках это заметно, показываем прогресс.
                if (autoClip && (r & 7) == 0)
                    Report($"Построение сетки… {r}/{rows}", 2 + (int)(3.0 * r / Math.Max(rows, 1)));

                for (int c = 0; c < cols; c++)
                {
                    double x0 = c * szX, y0 = r * szY;
                    var corners = new Point2d[4];
                    corners[0] = ToUcs2d(LW(x0,       y0,       cA, sA));
                    corners[1] = ToUcs2d(LW(x0 + szX, y0,       cA, sA));
                    corners[2] = ToUcs2d(LW(x0 + szX, y0 + szY, cA, sA));
                    corners[3] = ToUcs2d(LW(x0,       y0 + szY, cA, sA));

                    // Классификацию по границам делаем ДО проверки перекрытия,
                    // чтобы не тратить плотный досэмплинг на ячейки, которые всё
                    // равно отбрасываются (снаружи внешней границы или целиком в «дырке»).
                    CellClass outerCls = CellClass.Inside;
                    var partialInners = new List<Polyline>();
                    bool insideAnyInner = false;
                    if (hasManualBounds)
                    {
                        // Классификация по внешней границе — работает всегда.
                        outerCls = (outerPts != null)
                            ? ClassifyCell(corners, outerPts)
                            : CellClass.Inside;
                        if (outerCls == CellClass.Outside) continue;

                        // Классификация по внутренним границам: если ячейка целиком
                        // внутри хотя бы одной — пропускаем; иначе собираем пересекающиеся.
                        for (int k = 0; k < innerPtsList.Count; k++)
                        {
                            var iCls = ClassifyCell(corners, innerPtsList[k]);
                            if (iCls == CellClass.Inside) { insideAnyInner = true; break; }
                            if (iCls == CellClass.Partial) partialInners.Add(innerProtos[k]);
                        }
                        if (insideAnyInner) continue;
                    }

                    // Базовый фильтр: ячейка должна перекрываться обеими поверхностями.
                    // Грубая 3×3 выборка может не заметить узкую полосу перекрытия
                    // (тонкая траншея в крупной ячейке) — досэмплируем плотнее,
                    // прежде чем отбросить ячейку.
                    if (!CellHasOverlap(r, c, s1, s2, cA, sA))
                    {
                        if (!allowDense ||
                            !CellHasOverlap(r, c, s1, s2, cA, sA, DenseOverlapSteps()))
                            continue;
                    }

                    if (hasManualBounds)
                    {
                        // Флаг ClipCells управляет только тем, резать ли крайние
                        // ячейки. Если флаг снят — Partial-ячейки рисуются целиком.
                        bool needRegion = _o.ClipCells &&
                                          ((outerCls == CellClass.Partial) || partialInners.Count > 0);
                        if (needRegion)
                        {
                            var clipOuter = (outerCls == CellClass.Partial) ? outerProto : null;
                            var clippedRegion = BuildClippedCellRegion(
                                corners, clipOuter, partialInners, _doc.Editor, ref _clipErrorReported);
                            if (clippedRegion != null)
                            {
                                clippedRegion.Layer = _o.GridLayerName;
                                _ms.AppendEntity(clippedRegion);
                                t.AddNewlyCreatedDBObject(clippedRegion, true);
                                drawn++;
                            }
                            else
                            {
                                clipFailed++;
                            }
                            continue;
                        }
                        // Fall through: целиком внутри outer без пересечений,
                        // либо ClipCells=false и ячейка только задета границей.
                    }

                    if (autoClip)
                    {
                        var clipped = BuildAutoClippedCell(r, c, cA, sA, s1, s2);
                        if (clipped != null)
                        {
                            foreach (var loop in clipped)
                            {
                                var cpl = new Polyline();
                                for (int k = 0; k < loop.Count; k++)
                                    cpl.AddVertexAt(k, loop[k], 0, 0, 0);
                                cpl.Closed = true;
                                cpl.Layer  = _o.GridLayerName;
                                _ms.AppendEntity(cpl);
                                t.AddNewlyCreatedDBObject(cpl, true);
                            }
                            drawn++;
                            continue;
                        }
                        // null → ячейка целиком в зоне данных: обычный квадрат.
                    }

                    var pl = new Polyline();
                    pl.AddVertexAt(0, corners[0], 0, 0, 0);
                    pl.AddVertexAt(1, corners[1], 0, 0, 0);
                    pl.AddVertexAt(2, corners[2], 0, 0, 0);
                    pl.AddVertexAt(3, corners[3], 0, 0, 0);
                    pl.Closed = true;
                    pl.Layer  = _o.GridLayerName;
                    _ms.AppendEntity(pl);
                    t.AddNewlyCreatedDBObject(pl, true);
                    drawn++;
                }
            }

            outerProto?.Dispose();
            foreach (var ip in innerProtos) ip.Dispose();

            if (clipFailed > 0)
                _doc.Editor.WriteMessage(
                    $"\n[Картограмма] ВНИМАНИЕ: {clipFailed} краевых ячеек не удалось " +
                    "обрезать по границе (см. сообщения об ошибке клиппинга выше) — " +
                    "эти ячейки не нарисованы вовсе, а не оставлены целыми.");

            return drawn;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  ОБРЕЗКА ПО АВТОМАТИЧЕСКОЙ ГРАНИЦЕ (режим «Границы автоматически»)
        //
        //  «Автоматическая граница» — не полилиния на чертеже, а фактический
        //  край зоны данных: контур области, где заданы ОБЕ поверхности.
        //  Контур внутри ячейки строит CellClipper (marching squares + бисекция).
        //
        //  Обрезка ЧИСТО ВИЗУАЛЬНАЯ: объём (CalcCellVolumeAccurate /
        //  CalcCellVolumeSquares), площадь (CalcCellEffectiveArea) и подписи
        //  отметок (DrawNodeLabel) в авто-режиме считаются по наличию данных и
        //  от флага «Не обрезать» не зависят — см. IsInBounds/IsInClipRegion.
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Плотность выборки при обрезке по авто-границе. Чётная, чтобы
        /// узлы грубой проверки перекрытия (доли 0 / 0.5 / 1) попадали в выборку:
        /// ячейка, признанная «с данными», не должна оказаться пустой.</summary>
        private int ClipSampleSteps()
        {
            int n = DenseOverlapSteps();
            return (n % 2 == 0) ? n : n + 1;
        }

        // Плотность выборки контуров штриховки и линии нулевых работ — шаг
        // расчёта объёмов (VolumeNodeStep), чтобы штриховка делила ячейку так
        // же, как объёмы: узкий перешеек выемки между двумя пятнами насыпи не
        // проскакивает между узлами. Плотно сэмплируются только ячейки на
        // нулевой линии/краю — однородные штрихуются прямоугольником.
        /// <summary>Допуск края штриховки и линии нулевых работ: между узлами
        /// выборки край уточняется по изломам TIN до 1 мкм.</summary>
        private const double ExactEdgeTol = 1e-6;

        private int HatchSampleSteps()
        {
            int n = (int)Math.Ceiling(
                Math.Max(_o.CellSizeX, _o.CellSizeY) / Math.Max(_o.VolumeNodeStep, 1e-6));
            n = Clamp(n, 4, 120);
            return n + (n & 1);
        }

        /// <summary>
        /// Контуры ячейки (r, c), обрезанной по краю зоны данных, в координатах UCS.
        /// null — обрезать нечего: ячейка целиком в зоне данных (или контур
        /// построить не удалось), рисуется обычный квадрат.
        /// </summary>
        private List<List<Point2d>>? BuildAutoClippedCell(int r, int c,
            double cA, double sA, CivilSurface s1, CivilSurface s2)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            double x0 = c * szX, y0 = r * szY;

            // Предикат «есть данные» в локальных координатах ячейки.
            bool HasData(double lx, double ly)
            {
                double gx = x0 + lx, gy = y0 + ly;
                double wx = _o.BaseX + gx * cA - gy * sA;
                double wy = _o.BaseY + gx * sA + gy * cA;
                return GetElevS(s1, wx, wy).HasValue && GetElevS(s2, wx, wy).HasValue;
            }

            // Быстрый отсев: ячеек в глубине зоны данных подавляющее большинство,
            // и резать их не нужно. Грубая выборка 5×5 отсеивает их дёшево —
            // плотная выборка тратится только на краевые ячейки. Ценой этого
            // остаётся необрезанной ячейка, у которой край зоны срезает угол
            // мельче четверти стороны; на глаз такой срез и так неразличим.
            const int Coarse = 4;
            bool allInside = true;
            for (int i = 0; i <= Coarse && allInside; i++)
            for (int j = 0; j <= Coarse && allInside; j++)
                if (!HasData(j * szX / Coarse, i * szY / Coarse)) allInside = false;
            if (allInside) return null;

            var fill = CellClipper.Clip(szX, szY, ClipSampleSteps(), CellClipper.EdgeRefineSteps,
                HasData, out var loops);
            if (fill != CellClipper.CellFill.Partial) return null;

            var res = new List<List<Point2d>>(loops.Count);
            foreach (var loop in loops)
            {
                var pts = new List<Point2d>(loop.Count);
                foreach (var p in loop)
                    pts.Add(ToUcs2d(LW(x0 + p.X, y0 + p.Y, cA, sA)));
                res.Add(pts);
            }
            return res;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  ШТРИХОВКА ВЫЕМКИ/НАСЫПИ И ЛИНИЯ НУЛЕВЫХ РАБОТ
        //
        //  Область штриховки — там, где рабочая отметка нужного знака. Признак
        //  тот же, по которому объём делится между насыпью и выемкой
        //  (ZeroLineSplit), а границей служит та же нулевая линия
        //  (ZeroContour) — картинка и цифры не расходятся по построению.
        //
        //  Штрихуется поячеечно: контур области внутри ячейки строит CellClipper.
        //  Швов на границах квадратов не возникает — образец штриховки в AutoCAD
        //  привязан к началу координат, а не к контуру, поэтому соседние
        //  штриховки совпадают рисунком.
        //
        //  Ничего не вычисляет: объёмы, отметки и таблица уже посчитаны и от
        //  штриховки не зависят.
        // ═══════════════════════════════════════════════════════════════════════
        /// <param name="signAreas">Площади насыпной и выемочной частей ячеек
        /// из расчёта объёмов (ключ — строка, колонка): точный признак того,
        /// что в ячейке есть оба знака, даже если грубая разведка их не видит.</param>
        private void DrawHatchAndZeroLine(Transaction t, int rows, int cols,
            double cA, double sA, CivilSurface s1, CivilSurface s2,
            Dictionary<(int, int), (double Fill, double Cut)>? signAreas = null)
        {
            bool wantFill = _o.HatchFill.Enabled;
            bool wantCut  = _o.HatchCut.Enabled;
            bool wantZero = _o.ZeroLine.Enabled;
            if (!wantFill && !wantCut && !wantZero) return;

            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            int    steps = HatchSampleSteps();
            // Пятно меньше этого (доля площади ячейки) — шум численного
            // интегрирования у нулевой линии, вторым знаком не считаем.
            double minSignArea = szX * szY * 1e-4;
            var    ltId  = ResolveLinetype(t, _o.ZeroLine.LineType);
            bool   errorReported = false;
            int    hatches = 0, zeroChains = 0, hatchFallbacks = 0, exactCells = 0;
            var    mismatches = new List<string>();

            void DrawZeroChain(List<(double X, double Y)> chain, double x0, double y0)
            {
                if (chain.Count < 2) return;
                var pl = new Polyline();
                for (int k = 0; k < chain.Count; k++)
                    pl.AddVertexAt(k,
                        ToUcs2d(LW(x0 + chain[k].X, y0 + chain[k].Y, cA, sA)),
                        0, 0, 0);
                pl.Layer = _o.ZeroLineLayerName;
                pl.Color = Color.FromColorIndex(
                    ColorMethod.ByAci, (short)_o.ZeroLine.ColorAci);
                if (!ltId.IsNull) pl.LinetypeId = ltId;
                pl.LineWeight = ToLineWeight(_o.ZeroLine.LineWeight);
                _ms.AppendEntity(pl);
                t.AddNewlyCreatedDBObject(pl, true);
                zeroChains++;
            }

            // Сверка: заштрихованная площадь против площадей насыпи и
            // выемки из расчёта объёмов (они по той же поверхности).
            // Ячейки с зонами перепадов пропускаем — зоны из объёмов
            // ячейки вырезаны, а штриховка их покрывает.
            void CheckAreas(int r, int c, double x0, double y0, double hFill, double hCut)
            {
                if (signAreas == null || !signAreas.TryGetValue((r, c), out var chk) ||
                    CellTouchesAnomalyZone(x0, y0, szX, szY)) return;
                double tol = Math.Max(0.01, szX * szY * 0.005);
                if (wantFill && Math.Abs(hFill - chk.Fill) > tol)
                    mismatches.Add($"строка {r + 1}, колонка {c + 1}: насыпь — штриховка {hFill:F3} м², расчёт {chk.Fill:F3} м²");
                if (wantCut && Math.Abs(hCut - chk.Cut) > tol)
                    mismatches.Add($"строка {r + 1}, колонка {c + 1}: выемка — штриховка {hCut:F3} м², расчёт {chk.Cut:F3} м²");
            }

            // Точное разделение ячеек — чистые вычисления по снимкам TIN
            // (потокобезопасны), считаем заранее по всем ядрам.
            TinOverlay.Result?[]? exactAll = null;
            if (_snap1 != null && _snap2 != null)
            {
                Report("Штриховка: точное разделение по треугольникам TIN…", 90);
                exactAll = new TinOverlay.Result?[rows * cols];
                System.Threading.Tasks.Parallel.For(0, rows * cols, k =>
                    exactAll[k] = ExactCellSplit((k % cols) * szX, (k / cols) * szY, cA, sA));
            }

            for (int r = 0; r < rows; r++)
            {
                if ((r & 3) == 0)
                    Report($"Штриховка… {r}/{rows}", 90 + (int)(1.0 * r / Math.Max(rows, 1)));

                for (int c = 0; c < cols; c++)
                {
                    // Ровно те же ячейки, что и в сетке.
                    if (!IsCellDrawn(r, c, cA, sA)) continue;
                    if (!CellHasOverlap(r, c, s1, s2, cA, sA) &&
                        !CellHasOverlap(r, c, s1, s2, cA, sA, DenseOverlapSteps())) continue;

                    double x0 = c * szX, y0 = r * szY;

                    // Точно — по треугольникам обеих TIN, без выборки (до долей
                    // микрона, узкие пятна не теряются). Если нельзя (не TIN,
                    // ячейка на заданной границе, вырожденная склейка) — ниже
                    // прежний расчёт по выборке.
                    var exact = exactAll?[r * cols + c];
                    if (exact != null)
                    {
                        exactCells++;
                        double eFill = 0, eCut = 0;
                        if (wantFill && exact.Fill.Count > 0 &&
                            AddHatch(t, CellLoopsToUcs(exact.Fill, x0, y0, cA, sA), _o.HatchFill, ref errorReported))
                        { hatches++; eFill = exact.FillArea; }
                        if (wantCut && exact.Cut.Count > 0 &&
                            AddHatch(t, CellLoopsToUcs(exact.Cut, x0, y0, cA, sA), _o.HatchCut, ref errorReported))
                        { hatches++; eCut = exact.CutArea; }
                        if (wantZero)
                            foreach (var chain in exact.Zero) DrawZeroChain(chain, x0, y0);
                        CheckAreas(r, c, x0, y0, eFill, eCut);
                        continue;
                    }

                    // Рабочая отметка в локальных координатах ячейки.
                    // NaN — работ здесь нет: либо нет данных одной из
                    // поверхностей, либо точка вне заданных границ.
                    double H(double lx, double ly)
                    {
                        double gx = x0 + lx, gy = y0 + ly;
                        double wx = _o.BaseX + gx * cA - gy * sA;
                        double wy = _o.BaseY + gx * sA + gy * cA;
                        if (!IsInClipRegion(wx, wy)) return double.NaN;
                        double? e1 = GetElevS(s1, wx, wy);
                        double? e2 = GetElevS(s2, wx, wy);
                        if (!e1.HasValue || !e2.HasValue) return double.NaN;
                        return e2.Value - e1.Value;
                    }

                    // Грубая разведка знаков — одна на обе штриховки и линию.
                    // Ячейки целиком одного знака (их большинство) штрихуются
                    // прямоугольником без плотной выборки.
                    const int Coarse = 4;
                    int nPos = 0, nNeg = 0, nNan = 0;
                    for (int i = 0; i <= Coarse; i++)
                    for (int j = 0; j <= Coarse; j++)
                    {
                        double h = H(j * szX / Coarse, i * szY / Coarse);
                        if (double.IsNaN(h)) nNan++;
                        else if (h > 0)      nPos++;
                        else if (h < 0)      nNeg++;
                    }

                    // Уточнение по расчёту объёмов: пятно второго знака между
                    // точками разведки (островок насыпи в выемке) иначе
                    // осталось бы незамеченным, и ячейку целиком залила бы
                    // штриховка первого знака — прямо поверх островка.
                    bool anyPos = nPos > 0, anyNeg = nNeg > 0;
                    bool fillDominant = nPos >= nNeg;
                    if (signAreas != null && signAreas.TryGetValue((r, c), out var sa))
                    {
                        if (sa.Fill > minSignArea) anyPos = true;
                        if (sa.Cut  > minSignArea) anyNeg = true;
                        if (sa.Fill + sa.Cut > 0) fillDominant = sa.Fill >= sa.Cut;
                    }

                    // dominant — знак, которого в ячейке больше: если контур его
                    // части не построился, ячейка штрихуется им целиком (только
                    // одним знаком — две штриховки не наложатся). Только для
                    // ячеек без края данных/границы — иначе целый квадрат
                    // вылез бы за контур.
                    double hFill = 0, hCut = 0;
                    if (wantFill && anyPos &&
                        DrawSignHatch(t, x0, y0, cA, sA, steps, H, true,
                            !anyNeg && nNan == 0, nNan == 0 && fillDominant, _o.HatchFill,
                            ref errorReported, ref hatchFallbacks, out hFill))
                        hatches++;

                    if (wantCut && anyNeg &&
                        DrawSignHatch(t, x0, y0, cA, sA, steps, H, false,
                            !anyPos && nNan == 0, nNan == 0 && !fillDominant, _o.HatchCut,
                            ref errorReported, ref hatchFallbacks, out hCut))
                        hatches++;

                    CheckAreas(r, c, x0, y0, hFill, hCut);

                    if (wantZero && anyPos && anyNeg)
                        foreach (var chain in ZeroContour.Trace(szX, szY, steps, H, ExactEdgeTol))
                            DrawZeroChain(chain, x0, y0);
                }
            }

            _doc.Editor.WriteMessage(
                $"\n[Картограмма] Штриховка: {hatches} контуров, линия нулевых работ: {zeroChains} сегментов" +
                $" (точно по треугольникам TIN: {exactCells} ячеек)");
            if (hatchFallbacks > 0)
                _doc.Editor.WriteMessage(
                    $"\n[Картограмма] Штриховка: в {hatchFallbacks} ячейк(ах) контур не построился — заштрихованы целиком преобладающим знаком");
            if (mismatches.Count > 0)
            {
                _doc.Editor.WriteMessage(
                    $"\n[Картограмма] Сверка штриховки с площадями объёмов: расхождение в {mismatches.Count} случа(ях):");
                foreach (var m in mismatches.Take(30))
                    _doc.Editor.WriteMessage("\n    " + m);
                if (mismatches.Count > 30)
                    _doc.Editor.WriteMessage($"\n    … и ещё {mismatches.Count - 30}");
            }
            else if (signAreas != null && (wantFill || wantCut))
                _doc.Editor.WriteMessage(
                    "\n[Картограмма] Сверка штриховки с площадями объёмов: расхождений нет");
        }

        /// <summary>
        /// Точное разделение ячейки на насыпь/выемку по треугольникам обеих TIN
        /// (TinOverlay). null — точный путь неприменим: нет снимков TIN, ячейка
        /// задевает заданную границу (обрезку по ней ведёт выборка), или склейка
        /// не сошлась.
        /// </summary>
        private TinOverlay.Result? ExactCellSplit(double x0, double y0, double cA, double sA)
        {
            if (_snap1 == null || _snap2 == null) return null;
            try { return ExactCellSplitCore(x0, y0, cA, sA); }
            catch { return null; }
        }

        private TinOverlay.Result? ExactCellSplitCore(double x0, double y0, double cA, double sA)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            var cw = new[]
            {
                LW(x0, y0, cA, sA), LW(x0 + szX, y0, cA, sA),
                LW(x0 + szX, y0 + szY, cA, sA), LW(x0, y0 + szY, cA, sA),
            };
            if (_boundaryPts != null || _innerPtsList != null)
            {
                var corners = cw.Select(p => new Point2d(p.X, p.Y)).ToArray();
                if (_boundaryPts != null && ClassifyCell(corners, _boundaryPts) != CellClass.Inside)
                    return null;
                if (_innerPtsList != null)
                    foreach (var ip in _innerPtsList)
                        if (ClassifyCell(corners, ip) != CellClass.Outside) return null;
            }

            double mnX = cw.Min(p => p.X), mxX = cw.Max(p => p.X);
            double mnY = cw.Min(p => p.Y), mxY = cw.Max(p => p.Y);
            List<TinOverlay.Tri> Local(TinSnapshot snap)
            {
                var l = new List<TinOverlay.Tri>();
                double LX(double wx, double wy) => (wx - _o.BaseX) * cA + (wy - _o.BaseY) * sA - x0;
                double LY(double wx, double wy) => -(wx - _o.BaseX) * sA + (wy - _o.BaseY) * cA - y0;
                snap.ForEachTriangle(mnX, mnY, mxX, mxY, (ax, ay, az, bx, by, bz, qx, qy, qz) =>
                    l.Add(new TinOverlay.Tri(
                        LX(ax, ay), LY(ax, ay), az,
                        LX(bx, by), LY(bx, by), bz,
                        LX(qx, qy), LY(qx, qy), qz)));
                return l;
            }
            return TinOverlay.Build(szX, szY, Local(_snap1!), Local(_snap2!));
        }

        private List<List<Point2d>> CellLoopsToUcs(List<List<(double X, double Y)>> loops,
            double x0, double y0, double cA, double sA)
        {
            var res = new List<List<Point2d>>(loops.Count);
            foreach (var loop in loops)
            {
                var pts = new List<Point2d>(loop.Count);
                foreach (var p in loop) pts.Add(ToUcs2d(LW(x0 + p.X, y0 + p.Y, cA, sA)));
                res.Add(pts);
            }
            return res;
        }

        private bool CellTouchesAnomalyZone(double x0, double y0, double w, double h)
        {
            if (_anomalyZones == null) return false;
            foreach (var z in _anomalyZones)
                if (z.x1 > x0 && z.x0 < x0 + w && z.y1 > y0 && z.y0 < y0 + h) return true;
            return false;
        }

        /// <summary>
        /// Заштриховать часть ячейки с рабочей отметкой заданного знака.
        /// Возвращает true, если штриховка создана.
        /// </summary>
        private bool DrawSignHatch(Transaction t, double x0, double y0,
            double cA, double sA, int steps, Func<double, double, double> h,
            bool positive, bool wholeCell, bool dominant, HatchSpec spec,
            ref bool errorReported, ref int fallbacks, out double hatchedArea)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            hatchedArea = 0;
            var loops = new List<List<Point2d>>();
            bool partialDone = false;

            if (!wholeCell)
            {
                bool Pred(double lx, double ly)
                {
                    double v = h(lx, ly);
                    if (double.IsNaN(v)) return false;
                    return positive ? v > 0 : v < 0;
                }

                // Full здесь — либо плотная выборка вся этого знака (грубая
                // разведка зацепила мелкое пятно другого знака), либо
                // вырожденная сшивка контура. Ячейку без края данных не
                // оставляем пустой: её целиком берёт преобладающий знак.
                var fill = CellClipper.Clip(szX, szY, steps, CellClipper.EdgeRefineSteps,
                    Pred, out var raw, ExactEdgeTol);
                if (fill == CellClipper.CellFill.Partial && raw.Count > 0)
                {
                    foreach (var loop in raw)
                    {
                        var pts = new List<Point2d>(loop.Count);
                        foreach (var p in loop)
                            pts.Add(ToUcs2d(LW(x0 + p.X, y0 + p.Y, cA, sA)));
                        loops.Add(pts);
                        // Контуры ориентированы: внешние — против часовой (+),
                        // дырки — по часовой (−); сумма — площадь области.
                        hatchedArea += CellClipper.Area(loop);
                    }
                    partialDone = true;
                }
                else if (fill == CellClipper.CellFill.Empty || !dominant)
                    return false;
                else
                    fallbacks++;
            }

            if (!partialDone)
            {
                hatchedArea = szX * szY;
                loops.Add(new List<Point2d>
                {
                    ToUcs2d(LW(x0,       y0,       cA, sA)),
                    ToUcs2d(LW(x0 + szX, y0,       cA, sA)),
                    ToUcs2d(LW(x0 + szX, y0 + szY, cA, sA)),
                    ToUcs2d(LW(x0,       y0 + szY, cA, sA)),
                });
            }

            bool ok = AddHatch(t, loops, spec, ref errorReported);
            if (!ok) hatchedArea = 0;
            return ok;
        }

        /// <summary>Создать объект штриховки AutoCAD по готовым контурам.</summary>
        private bool AddHatch(Transaction t, List<List<Point2d>> loops,
            HatchSpec spec, ref bool errorReported)
        {
            if (loops.Count == 0) return false;

            Hatch? hat = null;
            try
            {
                hat = new Hatch();
                _ms.AppendEntity(hat);
                t.AddNewlyCreatedDBObject(hat, true);

                hat.SetDatabaseDefaults();
                // Порядок важен: и масштаб, и угол вступают в силу только после
                // ПОВТОРНОГО назначения образца — особенность API штриховки.
                // Поэтому оба параметра выставляются ДО второго SetHatchPattern,
                // иначе угол молча остаётся нулевым.
                hat.SetHatchPattern(HatchPatternType.PreDefined, spec.Pattern);
                hat.PatternScale = spec.Scale > 0 ? spec.Scale : 1.0;
                // Угол отсчитывается вместе с поворотом картограммы: штриховка
                // должна лежать по сетке, а не по мировым осям.
                hat.PatternAngle = (spec.Angle + _o.RotationDegrees) * Math.PI / 180.0;
                hat.SetHatchPattern(HatchPatternType.PreDefined, spec.Pattern);

                hat.Layer = _o.HatchLayerName;
                hat.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)spec.ColorAci);
                hat.Associative = false;

                foreach (var loop in loops)
                {
                    if (loop.Count < 3) continue;
                    var pts = new Point2dCollection();
                    foreach (var p in loop) pts.Add(p);
                    if (!loop[0].IsEqualTo(loop[loop.Count - 1])) pts.Add(loop[0]);

                    var bulges = new DoubleCollection();
                    for (int i = 0; i < pts.Count; i++) bulges.Add(0.0);

                    hat.AppendLoop(HatchLoopTypes.Default, pts, bulges);
                }

                if (hat.NumberOfLoops == 0)
                {
                    hat.Erase();
                    return false;
                }

                hat.EvaluateHatch(true);
                return true;
            }
            catch (System.Exception ex)
            {
                if (!errorReported)
                {
                    _doc.Editor.WriteMessage(
                        $"\n[Картограмма] Не удалось построить штриховку «{spec.Pattern}»: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    errorReported = true;
                }
                try { hat?.Erase(); } catch { }
                return false;
            }
        }

        /// <summary>ObjectId типа линий по имени; ObjectId.Null — оставить как есть.</summary>
        private ObjectId ResolveLinetype(Transaction t, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            try
            {
                var table = (LinetypeTable)t.GetObject(_db.LinetypeTableId, OpenMode.ForRead);
                if (table.Has(name)) return table[name];
                _doc.Editor.WriteMessage(
                    $"\n[Картограмма] Тип линий «{name}» не загружен в чертёж — " +
                    "линия нулевых работ будет сплошной.");
            }
            catch { }
            return ObjectId.Null;
        }

        /// <summary>Миллиметры → перечисление весов линий AutoCAD.</summary>
        private static LineWeight ToLineWeight(double mm)
        {
            if (mm < 0) return LineWeight.ByLayer;
            int hundredths = (int)Math.Round(mm * 100.0);
            foreach (LineWeight lw in Enum.GetValues(typeof(LineWeight)))
                if ((int)lw == hundredths) return lw;
            return LineWeight.ByLayer;
        }

        /// <summary>Построить плоскую замкнутую полилинию в WCS Z=0 из точек
        /// контура (для Region). Точки — из GetBoundaryPoints, уже в WCS.</summary>
        private static Polyline BuildFlatPolyline(List<Point2d> pts)
        {
            var p = new Polyline();
            for (int i = 0; i < pts.Count; i++)
                p.AddVertexAt(i, pts[i], 0, 0, 0);
            p.Closed = true;
            return p;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Обрезать ячейку по границе через Region.BooleanOperation —
        //  встроенный в AutoCAD надёжный механизм пересечения 2D-областей.
        //  Возвращает Region (геометрически точное пересечение cell ∩ boundary)
        //  или null, если пересечение пустое.
        // ═══════════════════════════════════════════════════════════════════════
        private static Region? BuildClippedCellRegion(
            Point2d[] cellCorners,
            Polyline? outerProto,
            List<Polyline> innerProtos,
            Editor ed,
            ref bool errorReported)
        {
            // Строим cellRegion из ячейки, при необходимости пересекаем с outer,
            // затем последовательно вычитаем каждую внутреннюю границу.
            var cellPl = new Polyline();
            cellPl.AddVertexAt(0, cellCorners[0], 0, 0, 0);
            cellPl.AddVertexAt(1, cellCorners[1], 0, 0, 0);
            cellPl.AddVertexAt(2, cellCorners[2], 0, 0, 0);
            cellPl.AddVertexAt(3, cellCorners[3], 0, 0, 0);
            cellPl.Closed = true;

            Region? cellRegion = null;
            try
            {
                var cellCurves = new DBObjectCollection { cellPl };
                var cellRegs   = Region.CreateFromCurves(cellCurves);
                if (cellRegs.Count == 0)
                {
                    if (!errorReported)
                    {
                        ed.WriteMessage("\n[Картограмма] CreateFromCurves(cell) вернул пусто");
                        errorReported = true;
                    }
                    cellPl.Dispose();
                    return null;
                }
                cellRegion = (Region)cellRegs[0];

                if (outerProto != null)
                {
                    if (!ApplyBoolean(cellRegion, outerProto, BooleanOperationType.BoolIntersect, ed, ref errorReported))
                    {
                        cellRegion.Dispose();
                        cellPl.Dispose();
                        return null;
                    }
                    if (cellRegion.Area < 1e-9)
                    {
                        cellRegion.Dispose();
                        cellPl.Dispose();
                        return null;
                    }
                }

                foreach (var inner in innerProtos)
                {
                    if (!ApplyBoolean(cellRegion, inner, BooleanOperationType.BoolSubtract, ed, ref errorReported))
                    {
                        cellRegion.Dispose();
                        cellPl.Dispose();
                        return null;
                    }
                    if (cellRegion.Area < 1e-9)
                    {
                        cellRegion.Dispose();
                        cellPl.Dispose();
                        return null;
                    }
                }

                cellPl.Dispose();
                return cellRegion;
            }
            catch (System.Exception ex)
            {
                if (!errorReported)
                {
                    ed.WriteMessage($"\n[Картограмма] Ошибка клиппинга: {ex.GetType().Name}: {ex.Message}");
                    errorReported = true;
                }
                cellRegion?.Dispose();
                try { cellPl.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>Применить boolean-операцию к cellRegion с region'ом из proto.</summary>
        private static bool ApplyBoolean(Region cellRegion, Polyline proto,
            BooleanOperationType op, Editor ed, ref bool errorReported)
        {
            var clone = (Polyline)proto.Clone();
            Region? other = null;
            try
            {
                var curves = new DBObjectCollection { clone };
                var regs = Region.CreateFromCurves(curves);
                if (regs.Count == 0)
                {
                    if (!errorReported)
                    {
                        ed.WriteMessage("\n[Картограмма] CreateFromCurves(boundary) вернул пусто");
                        errorReported = true;
                    }
                    clone.Dispose();
                    return false;
                }
                other = (Region)regs[0];
                cellRegion.BooleanOperation(op, other);
                other.Dispose();
                clone.Dispose();
                return true;
            }
            catch (System.Exception ex)
            {
                if (!errorReported)
                {
                    ed.WriteMessage($"\n[Картограмма] Ошибка клиппинга ({op}): {ex.GetType().Name}: {ex.Message}");
                    errorReported = true;
                }
                other?.Dispose();
                try { clone.Dispose(); } catch { }
                return false;
            }
        }

        private static Point2d ToUcs2d(Point3d p) => new Point2d(p.X, p.Y);

        // ═══════════════════════════════════════════════════════════════════════
        //  Подписи отметок в ячейке
        // ═══════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Проверяет, рисуется ли ячейка (r,c) — та же логика что в BuildCells:
        /// ячейка пропускается, если полностью вне наружной границы или целиком
        /// внутри любой «дырки». В остальных случаях — рисуется.
        /// </summary>
        private bool IsCellDrawn(int r, int c, double cA, double sA)
        {
            if (_boundaryPts == null && _innerPtsList == null) return true;

            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            double x0 = c * szX, y0 = r * szY;
            var corners = new Point2d[4];
            corners[0] = ToUcs2d(LW(x0,       y0,       cA, sA));
            corners[1] = ToUcs2d(LW(x0 + szX, y0,       cA, sA));
            corners[2] = ToUcs2d(LW(x0 + szX, y0 + szY, cA, sA));
            corners[3] = ToUcs2d(LW(x0,       y0 + szY, cA, sA));

            if (_boundaryPts != null &&
                ClassifyCell(corners, _boundaryPts) == CellClass.Outside)
                return false;

            if (_innerPtsList != null)
                foreach (var ipts in _innerPtsList)
                    if (ClassifyCell(corners, ipts) == CellClass.Inside)
                        return false;

            return true;
        }

        /// <summary>
        /// Узел (nr, nc) рисуется, если хотя бы одна из 4 смежных ячеек рисуется.
        /// Угловые/краевые узлы имеют 1–2 соседа — этого достаточно.
        /// </summary>
        private bool IsAnyAdjacentCellDrawn(int nr, int nc, int rows, int cols,
            double cA, double sA)
        {
            for (int dr = -1; dr <= 0; dr++)
            for (int dc = -1; dc <= 0; dc++)
            {
                int r = nr + dr, c = nc + dc;
                if (r < 0 || r >= rows || c < 0 || c >= cols) continue;
                if (IsCellDrawn(r, c, cA, sA)) return true;
            }
            return false;
        }

        /// <summary>
        /// Отметки в точках пересечения границ (наружной и внутренних) с линиями
        /// сетки. Это «новые углы», созданные обрезкой ячеек по границе.
        /// В каждой такой точке — тройка отметок (чёрная/красная/рабочая),
        /// полученная семплированием поверхностей в самой точке обрезки.
        /// </summary>
        private void DrawBoundaryGridIntersectionLabels(
            Transaction t, int rows, int cols, double cA, double sA,
            CivilSurface s1, CivilSurface s2)
        {
            if (_boundaryPts == null && _innerPtsList == null) return;

            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            double maxLx = cols * szX, maxLy = rows * szY;
            const double eps = 1e-6;

            // Дедуп уже отрисованных точек (в локальных координатах, округлённо)
            var drawn = new HashSet<long>();
            long KeyOf(double lx, double ly)
            {
                int ix = (int)Math.Round(lx / 1e-3);
                int iy = (int)Math.Round(ly / 1e-3);
                return ((long)ix << 32) ^ (uint)iy;
            }

            void ProcessEdge(Point2d pA, Point2d pB)
            {
                // Переводим концы ребра в локальные координаты сетки
                double dxA = pA.X - _o.BaseX, dyA = pA.Y - _o.BaseY;
                double dxB = pB.X - _o.BaseX, dyB = pB.Y - _o.BaseY;
                double lxA =  dxA * cA + dyA * sA;
                double lyA = -dxA * sA + dyA * cA;
                double lxB =  dxB * cA + dyB * sA;
                double lyB = -dxB * sA + dyB * cA;

                // Пересечения с вертикальными линиями сетки x = c*szX
                if (Math.Abs(lxB - lxA) > eps)
                {
                    int cMin = (int)Math.Ceiling (Math.Min(lxA, lxB) / szX - eps);
                    int cMax = (int)Math.Floor   (Math.Max(lxA, lxB) / szX + eps);
                    for (int c = Math.Max(0, cMin); c <= Math.Min(cols, cMax); c++)
                    {
                        double xg = c * szX;
                        double tp = (xg - lxA) / (lxB - lxA);
                        if (tp < -eps || tp > 1 + eps) continue;
                        double ly = lyA + tp * (lyB - lyA);
                        if (ly < -eps || ly > maxLy + eps) continue;
                        TryDraw(xg, ly);
                    }
                }

                // Пересечения с горизонтальными линиями сетки y = r*szY
                if (Math.Abs(lyB - lyA) > eps)
                {
                    int rMin = (int)Math.Ceiling (Math.Min(lyA, lyB) / szY - eps);
                    int rMax = (int)Math.Floor   (Math.Max(lyA, lyB) / szY + eps);
                    for (int r = Math.Max(0, rMin); r <= Math.Min(rows, rMax); r++)
                    {
                        double yg = r * szY;
                        double tp = (yg - lyA) / (lyB - lyA);
                        if (tp < -eps || tp > 1 + eps) continue;
                        double lx = lxA + tp * (lxB - lxA);
                        if (lx < -eps || lx > maxLx + eps) continue;
                        TryDraw(lx, yg);
                    }
                }
            }

            void TryDraw(double lx, double ly)
            {
                // Пропускаем точки, совпадающие с узлами сетки — там уже есть отметка
                double rX = lx / szX, rY = ly / szY;
                if (Math.Abs(rX - Math.Round(rX)) < 1e-4 &&
                    Math.Abs(rY - Math.Round(rY)) < 1e-4) return;

                long key = KeyOf(lx, ly);
                if (!drawn.Add(key)) return;

                double wx = _o.BaseX + lx * cA - ly * sA;
                double wy = _o.BaseY + lx * sA + ly * cA;
                double? e1 = GetElevS(s1, wx, wy);
                double? e2 = GetElevS(s2, wx, wy);
                if (!e1.HasValue || !e2.HasValue) return;

                double sh      = _o.SmallTextHeight;
                double ang     = _o.RotationRadians;
                int    workAci = _o.ColorWork;
                string fmt     = "F" + _o.TextPrecision;
                double margin  = sh * 0.15;
                double work    = e2.Value - e1.Value;

                RecordLabelBox(AddRightAlignedText(t, _o.WorkLayerName,
                    LW(lx - margin, ly + margin, cA, sA),
                    Signed(work, _o.TextPrecision), sh, ang, workAci, _o.HideMaskText));
                RecordLabelBox(AddTextToLayer(t, _o.DesignLayerName,
                    LW(lx + margin, ly + margin, cA, sA),
                    Num(e2.Value, _o.TextPrecision), sh, ang, _o.ColorDesign, hideMask: _o.HideMaskText));
                RecordLabelBox(AddTextToLayer(t, _o.ExistLayerName,
                    LW(lx + margin, ly - margin - sh, cA, sA),
                    Num(e1.Value, _o.TextPrecision), sh, ang, _o.ColorExisting, hideMask: _o.HideMaskText));
            }

            void ProcessPolygon(List<Point2d> pts)
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    var pA = pts[i];
                    var pB = pts[(i + 1) % pts.Count];
                    ProcessEdge(pA, pB);
                }
            }

            if (_boundaryPts != null) ProcessPolygon(_boundaryPts);
            if (_innerPtsList != null)
                foreach (var inner in _innerPtsList)
                    ProcessPolygon(inner);
        }

        /// <summary>
        /// Подбирает якорь метки, гарантированно попадающий внутрь ВИДИМОЙ части
        /// ячейки — той, что осталась после обрезки. Что считать видимой частью,
        /// зависит от режима границ:
        ///   • ручные границы — клип-область (наружная минус внутренние «дырки»).
        ///     Проверка чисто геометрическая и работает в ОБОИХ режимах флага:
        ///     даже в «Не обрезать» цифра объёма не должна улетать за заданные
        ///     границы (у траншеи-рамки центр крупной ячейки часто в «дырке»);
        ///   • нет ручной границы (авто-режим или просто ничего не выбрано) +
        ///     «обрезать» — зона данных, то есть та же автоматическая граница,
        ///     по которой обрезаны сами квадраты (см. BuildAutoClippedCell).
        ///     Без этого цифра краевой ячейки оставалась бы в центре квадрата,
        ///     снаружи обрезанного контура;
        ///   • нет ручной границы + «не обрезать» — квадрат рисуется целиком,
        ///     центр ячейки и так внутри геометрии, якорь не двигаем.
        /// Если исходная точка уже внутри — возвращает её. Иначе сэмплирует
        /// ячейку плотно (шаг как у DenseOverlapSteps — чтобы не промахнуться
        /// мимо узкой полосы траншеи) и ставит якорь в центроид попавших внутрь
        /// точек — цифра ложится по середине видимого куска ячейки, а не на его
        /// край. Если центроид сам вне области (L-образный кусок в углу рамки) —
        /// берётся ближайшая к нему внутренняя точка. Если внутренних точек нет —
        /// исходная (fallback).
        ///
        /// Двигается только ПОЛОЖЕНИЕ подписи; значение объёма не затрагивается.
        /// </summary>
        private void FindInsideAnchor(
            double cellX0, double cellY0, double szX, double szY,
            double ancLx, double ancLy, double cA, double sA,
            CivilSurface s1, CivilSurface s2,
            out double outLx, out double outLy)
        {
            outLx = ancLx; outLy = ancLy;

            bool manual   = _boundaryPts != null || _innerPtsList != null;
            // Без ручной границы обрезаем по факту зоны данных независимо от
            // флага «Границы автоматически» — см. пояснение у autoClip в
            // DrawGridLines.
            bool autoClip = !manual && _o.ClipCells;
            if (!manual && !autoClip) return;

            bool Visible(double wx, double wy) => manual
                ? IsInClipRegion(wx, wy)
                : GetElevS(s1, wx, wy).HasValue && GetElevS(s2, wx, wy).HasValue;

            double awx = _o.BaseX + ancLx * cA - ancLy * sA;
            double awy = _o.BaseY + ancLx * sA + ancLy * cA;
            if (Visible(awx, awy)) return;

            int n = Math.Max(7, DenseOverlapSteps());
            var inLx = new List<double>(n * n);
            var inLy = new List<double>(n * n);
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double lx = cellX0 + szX * (j + 0.5) / n;
                double ly = cellY0 + szY * (i + 0.5) / n;
                double wx = _o.BaseX + lx * cA - ly * sA;
                double wy = _o.BaseY + lx * sA + ly * cA;
                if (!Visible(wx, wy)) continue;
                inLx.Add(lx); inLy.Add(ly);
            }
            if (inLx.Count == 0) return;               // fallback: исходная точка

            double cxL = 0, cyL = 0;
            for (int k = 0; k < inLx.Count; k++) { cxL += inLx[k]; cyL += inLy[k]; }
            cxL /= inLx.Count; cyL /= inLx.Count;

            double cwx = _o.BaseX + cxL * cA - cyL * sA;
            double cwy = _o.BaseY + cxL * sA + cyL * cA;
            if (Visible(cwx, cwy))
            {
                outLx = cxL; outLy = cyL;
                return;
            }

            // Центроид вне области (вогнутый кусок) — ближайшая к нему внутренняя
            double best = double.MaxValue;
            for (int k = 0; k < inLx.Count; k++)
            {
                double dx = inLx[k] - cxL, dy = inLy[k] - cyL;
                double d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; outLx = inLx[k]; outLy = inLy[k]; }
            }
        }

        /// <summary>
        /// Якоря для ДВУХ подписей в ячейке, через которую проходит нулевая
        /// линия: точка внутри насыпной части и точка внутри выемочной.
        /// Ячейка сэмплируется, точки разделяются по знаку рабочей отметки,
        /// якорем каждой части служит ближайшая к её центроиду точка этой же
        /// части. Именно «ближайшая точка набора», а не сам центроид: у
        /// серповидной части (изогнутая нулевая линия) центроид лежит по
        /// другую сторону линии, и цифра ушла бы в чужую половину.
        /// Пригодность точки проверяется так же, как у одиночной подписи
        /// (FindInsideAnchor): цифра не должна выходить за заданные границы.
        /// </summary>
        private void FindZeroLineAnchors(int r, int c, double cA, double sA,
            CivilSurface s1, CivilSurface s2,
            out double fillLx, out double fillLy,
            out double cutLx,  out double cutLy)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            double x0 = c * szX, y0 = r * szY;

            // Запасной вариант — центр ячейки, если своих точек не нашлось.
            fillLx = cutLx = x0 + szX * 0.5;
            fillLy = cutLy = y0 + szY * 0.5;

            int n = Math.Max(7, DenseOverlapSteps());
            var fx = new List<double>(); var fy = new List<double>();
            var cx = new List<double>(); var cy = new List<double>();

            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double lx = x0 + szX * (j + 0.5) / n;
                double ly = y0 + szY * (i + 0.5) / n;
                double wx = _o.BaseX + lx * cA - ly * sA;
                double wy = _o.BaseY + lx * sA + ly * cA;

                double? e1 = GetElevS(s1, wx, wy);
                double? e2 = GetElevS(s2, wx, wy);
                if (!e1.HasValue || !e2.HasValue) continue;
                if (!IsInClipRegion(wx, wy) || InAnomalyZoneW(wx, wy)) continue;

                double h = e2.Value - e1.Value;
                if      (h > 0) { fx.Add(lx); fy.Add(ly); }
                else if (h < 0) { cx.Add(lx); cy.Add(ly); }
            }

            PickPartAnchor(fx, fy, ref fillLx, ref fillLy);
            PickPartAnchor(cx, cy, ref cutLx,  ref cutLy);
        }

        /// <summary>Якорь части: ближайшая к центроиду точка самого набора —
        /// гарантированно лежит внутри своей части при любой её форме.</summary>
        private static void PickPartAnchor(List<double> xs, List<double> ys,
            ref double outX, ref double outY)
        {
            if (xs.Count == 0) return;

            double sx = 0, sy = 0;
            for (int k = 0; k < xs.Count; k++) { sx += xs[k]; sy += ys[k]; }
            double gx = sx / xs.Count, gy = sy / ys.Count;

            double best = double.MaxValue;
            for (int k = 0; k < xs.Count; k++)
            {
                double dx = xs[k] - gx, dy = ys[k] - gy;
                double d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; outX = xs[k]; outY = ys[k]; }
            }
        }

        /// <summary>
        /// Рисует тройку отметок (чёрная/красная/рабочая) в одном узле сетки.
        /// Узел — пересечение линий сетки (nr, nc), включая крайние. Узлы вне
        /// клип-области или вне обеих поверхностей пропускаются. Так покрываются
        /// все 4 угла каждой ячейки без дублирования на общих рёбрах.
        /// </summary>
        private void DrawNodeLabel(Transaction t, int nr, int nc,
            int rows, int cols, double cA, double sA,
            CivilSurface s1, CivilSurface s2)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            double nx  = nc * szX;
            double ny  = nr * szY;
            double wx  = _o.BaseX + nx * cA - ny * sA;
            double wy  = _o.BaseY + nx * sA + ny * cA;

            // В режиме «Обрезать» узел допустим только если он геометрически
            // в клип-области. В режиме «Не обрезать» квадраты рисуются целиком
            // даже если угол попал в «дырку» или за наружную — такому углу
            // отметка всё равно нужна. Критерий: узел отрисовывается, если
            // хотя бы одна из 4 смежных ячеек нарисована.
            // Условие про «дырку»/наружную осмысленно только при РУЧНЫХ границах.
            // В авто-режиме клип-области нет, и узел отбирается ниже по наличию
            // данных на обеих поверхностях — одинаково при любом положении флага
            // «Обрезать квадраты»: он там управляет только отрисовкой квадратов.
            if (!_o.ClipCells && !_o.AutoBounds)
            {
                if (!IsAnyAdjacentCellDrawn(nr, nc, rows, cols, cA, sA)) return;
            }
            else
            {
                if (!IsInClipRegion(wx, wy)) return;
            }

            double? e1 = GetElevS(s1, wx, wy);
            double? e2 = GetElevS(s2, wx, wy);
            if (!e1.HasValue || !e2.HasValue) return;

            double sh      = _o.SmallTextHeight;
            double ang     = _o.RotationRadians;
            int    workAci = _o.ColorWork;
            string fmt     = "F" + _o.TextPrecision;
            double margin  = sh * 0.15;
            double work    = e2.Value - e1.Value;

            // Рабочая — справа-налево, выше и левее узла
            RecordLabelBox(AddRightAlignedText(t, _o.WorkLayerName,
                LW(nx - margin, ny + margin, cA, sA),
                Signed(work, _o.TextPrecision), sh, ang, workAci, _o.HideMaskText));

            // Проектная — слева-направо, выше и правее узла
            RecordLabelBox(AddTextToLayer(t, _o.DesignLayerName,
                LW(nx + margin, ny + margin, cA, sA),
                Num(e2.Value, _o.TextPrecision), sh, ang, _o.ColorDesign, hideMask: _o.HideMaskText));

            // Существующая — слева-направо, ниже и правее узла
            RecordLabelBox(AddTextToLayer(t, _o.ExistLayerName,
                LW(nx + margin, ny - margin - sh, cA, sA),
                Num(e1.Value, _o.TextPrecision), sh, ang, _o.ColorExisting, hideMask: _o.HideMaskText));
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Итоговая таблица — нативный объект AutoCAD Table
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawSummaryTable(Transaction t, int rows, int cols,
            double cA, double sA,
            double[] colCut, double[] colFill,
            double[] rowCut, double[] rowFill,
            double totCut, double totFill,
            int rowOffset = 0, int colOffset = 0,
            double totalArea = 0, double cutArea = 0, double fillArea = 0,
            bool showCutLine = false, bool showFillLine = false)
        {
            var ed  = _doc.Editor;
            double szX = _o.CellSizeX, szY = _o.CellSizeY;

            // Исходные данные таблицы — PlaceTable сохранит их в самой таблице,
            // чтобы «Перерисовать» мог перестроить её под новый шрифт без
            // пересчёта объёмов (см. RebuildSummaryTables).
            var data = new List<double>
            {
                SummaryDataVersion, rows, cols, rowOffset, colOffset,
                totCut, totFill, totalArea, _o.BaseX, _o.BaseY,
                cutArea, fillArea, showCutLine ? 1 : 0, showFillLine ? 1 : 0
            };
            data.AddRange(colCut); data.AddRange(colFill);
            data.AddRange(rowCut); data.AddRange(rowFill);
            _summaryData = data.ToArray();

            // Высота шрифта таблицы — ровно как задал пользователь: все ячейки,
            // кроме колонок данных (они привязаны к шагу сетки), верстаются
            // «впритык» к тексту и масштабируются пропорционально шрифту.
            double sh  = _o.TableTextHeight;
            double ang = _o.RotationRadians;
            int    tc  = _o.ColorTable;

            // Смещение активной области в локальных координатах
            double offX = colOffset * szX;
            double offY = rowOffset * szY;

            // Размеры активной области сетки
            double gridWidth  = cols * szX;
            double gridHeight = rows * szY;

            // Первая строка — всегда; строки выемки/насыпи — только если
            // при расчёте была включена соответствующая штриховка.
            var areaLines = new List<string>
            {
                $"Площадь: {Num(totalArea)} м²  насыпь: +{Num(totFill)} м³  выемка: –{Num(totCut)} м³"
            };
            if (showCutLine)
                areaLines.Add($"Площадь выемки: {Num(cutArea)} м²  объём выемки: –{Num(totCut)} м³");
            if (showFillLine)
                areaLines.Add($"Площадь насыпи: {Num(fillArea)} м²  объём насыпи: +{Num(totFill)} м³");
            // Каждая строка — отдельный MText, привязанный низом; строки идут
            // вниз от первой. Там, где подпись стоит ПОД таблицей, опускаем её
            // низ на высоту добавочных строк — первая строка остаётся на месте.
            double lineGap    = sh * 5.0 / 3.0;
            double extraLines = (areaLines.Count - 1) * lineGap;

            int pos = _o.TablePosition;
            string[] posNames = { "Сверху", "Снизу", "Слева", "Справа" };

            ed.WriteMessage($"\n[Таблица] ════════════════════════════════════════════");
            ed.WriteMessage($"\n[Таблица] Позиция: {posNames[pos]}");
            ed.WriteMessage($"\n[Таблица] Сетка: {cols}×{rows} ячеек, размер ячейки: {szX}×{szY} м");
            ed.WriteMessage($"\n[Таблица] Ширина сетки: {gridWidth:F2} м, высота: {gridHeight:F2} м");
            ed.WriteMessage($"\n[Таблица] Высота шрифта таблицы: {sh:F2} м");
            ed.WriteMessage($"\n[Таблица] Угол поворота: {_o.RotationDegrees:F2}°");

            // ════════════════════════════════════════════════════════════════════
            //  ВЕРТИКАЛЬНАЯ ТАБЛИЦА  (Слева / Справа)
            //  Одна AutoCAD Table: [Итого,м³→]|[Насыпь↑|Выемка↑]|[данные]|[Всего,м³→]|[итоги]
            //  Вертикальный текст заголовков — через Contents[0].Rotation = PI/2.
            //  Строки данных совпадают по высоте и положению с рядами сетки.
            // ════════════════════════════════════════════════════════════════════
            if (pos == 2 || pos == 3)
            {
                double gap = szX;
                double pad = sh * 0.6;   // «воздух»: по 0.3 высоты текста с каждой стороны

                // Строки значений нужны заранее — по самым длинным меряется ширина колонок
                string sFillTot = totFill > 0.001 ? "+" + Num(totFill) : "–";
                string sCutTot  = totCut  > 0.001 ? "–" + Num(totCut)  : "–";
                string longFill = sFillTot, longCut = sCutTot;
                for (int gr = 0; gr < rows; gr++)
                {
                    string fs = rowFill[gr] > 0.001 ? "+" + Num(rowFill[gr]) : "–";
                    string cs = rowCut[gr]  > 0.001 ? "–" + Num(rowCut[gr])  : "–";
                    if (fs.Length > longFill.Length) longFill = fs;
                    if (cs.Length > longCut.Length)  longCut  = cs;
                }

                // ── Точная вёрстка по фактической ширине текста ──────────────
                // Ширину таблицы задают: самая длинная цифра в колонке (впритык)
                // и горизонтальные «Итого, м³»/«Всего, м³» на две колонки.
                double wItog  = MeasureTextWidth(t, ItogoM3, sh) + sh * 0.2;   // небольшой запас
                double wVsego = MeasureTextWidth(t, VsegoM3, sh) + sh * 0.2;
                double colW   = Math.Max(
                    Math.Max(MeasureTextWidth(t, longFill, sh),
                             MeasureTextWidth(t, longCut,  sh)) + pad,
                    (Math.Max(wItog, wVsego) + pad) / 2.0);

                double itogH  = sh + pad;                     // «Итого, м³» — одна строка впритык
                // «Насыпь»/«Выемка» вертикально — высота по длине текста впритык
                double labelH = Math.Max(MeasureTextWidth(t, "Насыпь", sh),
                                         MeasureTextWidth(t, "Выемка", sh)) + pad;
                double vsegoH = sh + pad;                     // «Всего, м³» — одна строка впритык
                double totalH = sh + pad;                     // строка итоговых цифр впритык

                // Индексы строк
                int iItog  = 0;
                int iLabel = 1;
                int iData0 = 2;
                int iVsego = 2 + rows;
                int iTotal = 2 + rows + 1;
                int tblRows = iTotal + 1;   // = rows + 4
                int tblCols = 2;            // Насыпь | Выемка

                double tableW = tblCols * colW;

                var rowHeights = new double[tblRows];
                rowHeights[iItog]  = itogH;
                rowHeights[iLabel] = labelH;
                for (int r = 0; r < rows; r++) rowHeights[iData0 + r] = szY;
                rowHeights[iVsego] = vsegoH;
                rowHeights[iTotal] = totalH;

                var tbl = CreateFixedTable(tblRows, tblCols);
                if (!_tblTsId.IsNull)
                    for (int r = 0; r < tblRows; r++)
                        for (int c = 0; c < tblCols; c++)
                            tbl.Cells[r, c].TextStyleId = _tblTsId;
                for (int c = 0; c < tblCols; c++) tbl.Columns[c].Width = colW;
                for (int r = 0; r < tblRows; r++) tbl.Rows[r].Height = rowHeights[r];

                // Данные: строка таблицы k → строка сетки (rows-1-k) сверху вниз
                for (int k = 0; k < rows; k++)
                {
                    int gr = rows - 1 - k;
                    string fs = rowFill[gr] > 0.001 ? "+" + Num(rowFill[gr]) : "–";
                    string cs = rowCut[gr]  > 0.001 ? "–" + Num(rowCut[gr])  : "–";
                    SetCell(tbl, iData0 + k, 0, fs, sh, CellAlignment.MiddleCenter, tc);
                    SetCell(tbl, iData0 + k, 1, cs, sh, CellAlignment.MiddleCenter, tc);
                }

                // Итоги
                SetCell(tbl, iTotal, 0, sFillTot, sh, CellAlignment.MiddleCenter, tc);
                SetCell(tbl, iTotal, 1, sCutTot,  sh, CellAlignment.MiddleCenter, tc);

                // Мержим «Итого, м³» и «Всего, м³» по двум столбцам
                tbl.MergeCells(CellRange.Create(tbl, iItog,  0, iItog,  1));
                tbl.MergeCells(CellRange.Create(tbl, iVsego, 0, iVsego, 1));

                tbl.GenerateLayout();

                // Восстановить размеры после GenerateLayout
                for (int c = 0; c < tblCols; c++) tbl.Columns[c].Width = colW;
                for (int r = 0; r < tblRows; r++) tbl.Rows[r].Height = rowHeights[r];

                // Заполнить мержнутые строки и установить вертикальный текст заголовков
                SetCell(tbl, iItog,  0, ItogoM3, sh, CellAlignment.MiddleCenter, tc);
                SetCell(tbl, iVsego, 0, VsegoM3, sh, CellAlignment.MiddleCenter, tc);
                SetCell(tbl, iLabel, 0, "Насыпь", sh, CellAlignment.MiddleCenter, tc);
                tbl.Cells[iLabel, 0].Contents[0].Rotation = Math.PI * 0.5;
                SetCell(tbl, iLabel, 1, "Выемка", sh, CellAlignment.MiddleCenter, tc);
                tbl.Cells[iLabel, 1].Contents[0].Rotation = Math.PI * 0.5;

                // Контрольное восстановление размеров: заполнение ячеек и поворот
                // контента могли снова включить авто-подгон
                for (int c = 0; c < tblCols; c++) tbl.Columns[c].Width = colW;
                for (int r = 0; r < tblRows; r++) tbl.Rows[r].Height = rowHeights[r];

                // Insertion Y = gridHeight + itogH + labelH → строки данных совпадают с рядами сетки
                double tblLocalX = offX + (pos == 2 ? -(tableW + gap) : gridWidth + gap);
                double tblLocalY = offY + gridHeight + itogH + labelH;

                Point3d tblPos = LW(tblLocalX, tblLocalY, cA, sA);
                var m = Matrix3d.Displacement(tblPos.GetAsVector());
                if (Math.Abs(ang) > 0.001)
                    m *= Matrix3d.Rotation(ang, Vector3d.ZAxis, Point3d.Origin);
                tbl.TransformBy(m);

                // Маскировка + таблица в одном блоке (если включён чек-бокс),
                // иначе просто таблица.
                double tblH = itogH + labelH + rows * szY + vsegoH + totalH;
                PlaceTable(t, tbl, tblLocalX, tblLocalY, tableW, tblH, cA, sA);

                // Текст итогов — ниже таблицы
                double txtY = offY - (vsegoH + totalH + sh * 1.5) - extraLines;
                AddSummaryLines(t, tblLocalX, txtY, areaLines, sh, ang, tc, lineGap, cA, sA);

                ed.WriteMessage($"\n[Таблица] Вертикальная: {tblRows}×{tblCols} (itogo+label+data+vsego+total)");
                ed.WriteMessage($"\n[Таблица] ════════════════════════════════════════════\n");
                return;
            }

            // ════════════════════════════════════════════════════════════════════
            //  ГОРИЗОНТАЛЬНАЯ ТАБЛИЦА  (Сверху / Снизу)
            //  Одна AutoCAD Table: [Итого,м³]|[Насыпь/Выемка]|[данные]|[Всего,м³]|[итоги]
            //  Вертикальный текст — через Cell.Rotation = PI/2 в мержнутых ячейках.
            //  Столбцы данных совпадают по ширине и положению с ячейками сетки.
            // ════════════════════════════════════════════════════════════════════
            {
                double gap = szY;
                double pad = sh * 0.6;   // «воздух»: по 0.3 высоты текста с каждой стороны

                // Итоговые строки нужны заранее — по ним меряется ширина ячеек
                string sFillTot = totFill > 0.001 ? "+" + Num(totFill) : "–";
                string sCutTot  = totCut  > 0.001 ? "–" + Num(totCut)  : "–";

                // ── Точная вёрстка по фактической ширине текста ──────────────
                // Высоту таблицы задаёт ВЕРТИКАЛЬНОЕ «Итого, м³»: две строки
                // делят его длину пополам — текст помещается впритык.
                double wItog  = MeasureTextWidth(t, ItogoM3, sh) + sh * 0.2;   // небольшой запас
                double wVsego = MeasureTextWidth(t, VsegoM3, sh) + sh * 0.2;
                double rH     = (Math.Max(wItog, wVsego) + pad) / 2.0;
                double tblH   = 2.0 * rH;

                // «Насыпь»/«Выемка» — по ширине текста впритык
                double labelW = Math.Max(MeasureTextWidth(t, "Насыпь", sh),
                                         MeasureTextWidth(t, "Выемка", sh)) + pad;

                // Колонки повёрнутого текста: ширина = высота строки текста
                double itogW  = sh + pad;
                double vsegoW = sh + pad;

                // Последняя колонка — впритык к итоговой цифре
                double totalW = Math.Max(MeasureTextWidth(t, sFillTot, sh),
                                         MeasureTextWidth(t, sCutTot,  sh)) + pad;

                // Индексы столбцов
                int iItog  = 0;
                int iLabel = 1;
                int iData0 = 2;
                int iVsego = 2 + cols;
                int iTotal = 2 + cols + 1;
                int tblRows = 2;
                int tblCols = iTotal + 1;   // = cols + 4

                var colWidths = new double[tblCols];
                colWidths[iItog]  = itogW;
                colWidths[iLabel] = labelW;
                for (int c = 0; c < cols; c++) colWidths[iData0 + c] = szX;
                colWidths[iVsego] = vsegoW;
                colWidths[iTotal] = totalW;

                var tbl = CreateFixedTable(tblRows, tblCols);
                if (!_tblTsId.IsNull)
                    for (int r = 0; r < tblRows; r++)
                        for (int c = 0; c < tblCols; c++)
                            tbl.Cells[r, c].TextStyleId = _tblTsId;
                for (int c = 0; c < tblCols; c++) tbl.Columns[c].Width = colWidths[c];
                for (int r = 0; r < tblRows; r++) tbl.Rows[r].Height = rH;

                // Насыпь (строка 0)
                SetCell(tbl, 0, iLabel, "Насыпь", sh, CellAlignment.MiddleCenter, tc);
                for (int c = 0; c < cols; c++)
                {
                    string val = colFill[c] > 0.001 ? "+" + Num(colFill[c]) : "–";
                    SetCell(tbl, 0, iData0 + c, val, sh, CellAlignment.MiddleCenter, tc);
                }
                SetCell(tbl, 0, iTotal, sFillTot, sh, CellAlignment.MiddleCenter, tc);

                // Выемка (строка 1)
                SetCell(tbl, 1, iLabel, "Выемка", sh, CellAlignment.MiddleCenter, tc);
                for (int c = 0; c < cols; c++)
                {
                    string val = colCut[c] > 0.001 ? "–" + Num(colCut[c]) : "–";
                    SetCell(tbl, 1, iData0 + c, val, sh, CellAlignment.MiddleCenter, tc);
                }
                SetCell(tbl, 1, iTotal, sCutTot, sh, CellAlignment.MiddleCenter, tc);

                // Мержим «Итого, м³» и «Всего, м³» по двум строкам
                tbl.MergeCells(CellRange.Create(tbl, 0, iItog,  1, iItog));
                tbl.MergeCells(CellRange.Create(tbl, 0, iVsego, 1, iVsego));

                tbl.GenerateLayout();

                // Восстановить размеры после GenerateLayout
                for (int c = 0; c < tblCols; c++) tbl.Columns[c].Width = colWidths[c];
                for (int r = 0; r < tblRows; r++) tbl.Rows[r].Height = rH;

                // Заполнить мержнутые ячейки и применить поворот текста
                SetCell(tbl, 0, iItog,  ItogoM3, sh, CellAlignment.MiddleCenter, tc);
                tbl.Cells[0, iItog].Contents[0].Rotation  = Math.PI * 0.5;
                SetCell(tbl, 0, iVsego, VsegoM3, sh, CellAlignment.MiddleCenter, tc);
                tbl.Cells[0, iVsego].Contents[0].Rotation = Math.PI * 0.5;

                // Контрольное восстановление размеров: заполнение ячеек и поворот
                // контента могли снова включить авто-подгон
                for (int c = 0; c < tblCols; c++) tbl.Columns[c].Width = colWidths[c];
                for (int r = 0; r < tblRows; r++) tbl.Rows[r].Height = rH;

                // Таблица начинается с X = offX-(itogW+labelW) → столбцы данных совпадают с активной областью
                double tblLocalX = offX - (itogW + labelW);
                double tblLocalY, txtLocalY;

                if (pos == 1) // Снизу
                {
                    tblLocalY = offY - gap;
                    txtLocalY = offY - gap - tblH - sh * 1.5 - extraLines;
                }
                else // Сверху (pos == 0)
                {
                    tblLocalY = offY + gridHeight + gap + tblH;
                    txtLocalY = offY + gridHeight + gap + tblH + sh * 1.5;
                }

                // Позиционирование таблицы
                Point3d tblPos = LW(tblLocalX, tblLocalY, cA, sA);
                var m = Matrix3d.Displacement(tblPos.GetAsVector());
                if (Math.Abs(ang) > 0.001)
                    m *= Matrix3d.Rotation(ang, Vector3d.ZAxis, Point3d.Origin);
                tbl.TransformBy(m);

                // Маскировка + таблица в одном блоке (если включён чек-бокс),
                // иначе просто таблица.
                double tblWfull = itogW + labelW + cols * szX + vsegoW + totalW;
                PlaceTable(t, tbl, tblLocalX, tblLocalY, tblWfull, tblH, cA, sA);

                // ── Текст итогов под/над таблицей ──────────────────────────────────
                AddSummaryLines(t, tblLocalX, txtLocalY, areaLines, sh, ang, tc, lineGap, cA, sA);

                ed.WriteMessage($"\n[Таблица] Горизонтальная: 2×{tblCols} (itogo+label+data+vsego+total)");
                ed.WriteMessage($"\n[Таблица] ════════════════════════════════════════════\n");
            }
        }

        /// <summary>
        /// Фактическая ширина строки текста в единицах чертежа для стиля таблицы
        /// и заданной высоты. Временный DBText добавляется в модель и сразу
        /// удаляется — единственный надёжный способ учесть метрику шрифта
        /// (ширины букв различаются у разных стилей). Используется для точной
        /// вёрстки итоговой таблицы «впритык» к тексту при любом размере шрифта.
        /// </summary>
        // Заголовки таблицы — с обычным юникодным «³», как принято на картограммах.
        private const string ItogoM3 = "Итого, м³";
        private const string VsegoM3 = "Всего, м³";

        private double MeasureTextWidth(Transaction t, string text, double height)
        {
            double w = 0;
            // DBText не понимает коды форматирования MText — меряем только MText
            if (text.IndexOf('\\') < 0)
            try
            {
                var txt = new DBText { TextString = text, Height = height };
                txt.SetDatabaseDefaults();
                if (!_tblTsId.IsNull) txt.TextStyleId = _tblTsId;
                _ms.AppendEntity(txt);
                t.AddNewlyCreatedDBObject(txt, true);
                var ext = txt.GeometricExtents;
                w = ext.MaxPoint.X - ext.MinPoint.X;
                txt.Erase();
            }
            catch { w = text.Length * height * 0.62; }   // грубая оценка по числу знаков

            // В ячейке таблицы текст рисуется как MText, а не DBText. Символы,
            // которых нет в SHX-шрифте стиля (например «³» в «м³»), MText берёт
            // из шрифта-замены, и они выходят шире, чем у DBText, — «Всего, м³»
            // вылезал за границу ячейки. Меряем и так, и так, берём большее.
            try
            {
                var mt = new MText { Contents = text, TextHeight = height, Width = 0 };
                mt.SetDatabaseDefaults();
                if (!_tblTsId.IsNull) mt.TextStyleId = _tblTsId;
                _ms.AppendEntity(mt);
                t.AddNewlyCreatedDBObject(mt, true);
                w = Math.Max(w, mt.ActualWidth);
                mt.Erase();
            }
            catch { }
            if (w <= 0) w = text.Length * height * 0.62;   // грубая оценка по числу знаков
            return w;
        }

        /// <summary>Создать таблицу с отключённым авто-подгоном размеров</summary>
        private Autodesk.AutoCAD.DatabaseServices.Table CreateFixedTable(int rows, int cols)
        {
            var tbl = new Autodesk.AutoCAD.DatabaseServices.Table();
            tbl.SetDatabaseDefaults();
            tbl.Layer = _o.TableLayerName;
            tbl.SetSize(rows, cols);
            // Обнуляем горизонтальный/вертикальный отступ текста в ячейках,
            // чтобы числа в одну строку помещались даже при минимальном размере
            // ячейки. Габариты ячеек контролируются явно через Columns/Rows.
            try { tbl.HorizontalCellMargin = 0.0; } catch { }
            try { tbl.VerticalCellMargin   = 0.0; } catch { }
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    // Per-cell text padding (override стиля таблицы) — единственный
                    // надёжный путь обнулить «Отступ по горизонтали/вертикали».
                    try { tbl.SetMargin(r, c, CellMargins.Left,   0.0); } catch { }
                    try { tbl.SetMargin(r, c, CellMargins.Right,  0.0); } catch { }
                    try { tbl.SetMargin(r, c, CellMargins.Top,    0.0); } catch { }
                    try { tbl.SetMargin(r, c, CellMargins.Bottom, 0.0); } catch { }

                    var cell = tbl.Cells[r, c];
                    cell.Borders.Top.Margin =
                    cell.Borders.Bottom.Margin =
                    cell.Borders.Left.Margin =
                    cell.Borders.Right.Margin = 0.0;
                }
            return tbl;
        }

        /// <summary>
        /// Размещает готовую (уже трансформированную в WCS) таблицу в чертеже.
        ///
        /// Если выключен чек-бокс «Скрывать задний план» (_o.HideMaskTable == false) —
        /// таблица просто добавляется в пространство модели.
        ///
        /// Если включён — маскировка (Wipeout) по периметру таблицы и сама таблица
        /// помещаются в ОДИН анонимный блок: маскировка добавляется первой (снизу),
        /// таблица — второй (сверху). Порядок добавления сущностей внутри блока
        /// задаёт порядок прорисовки, поэтому таблица всегда поверх своей маскировки.
        /// Затем блок вставляется в модель и поднимается на самый верх — так связка
        /// «маскировка + таблица» гарантированно оказывается поверх чертежа при любом
        /// положении таблицы (сверху/снизу/слева/справа) и любом размере шрифта.
        ///
        /// tblLocalX/Y — левый верхний угол таблицы в локальных координатах сетки.
        /// tableW/H    — ширина и высота таблицы.
        /// </summary>
        private void PlaceTable(Transaction t,
            Autodesk.AutoCAD.DatabaseServices.Table tbl,
            double tblLocalX, double tblLocalY,
            double tableW,    double tableH,
            double cA, double sA)
        {
            // Маскировка отключена — добавляем таблицу как есть.
            if (!_o.HideMaskTable)
            {
                _ms.AppendEntity(tbl);
                t.AddNewlyCreatedDBObject(tbl, true);
                SaveSummaryData(t, tbl);
                return;
            }

            // ── Маскировка по периметру таблицы (WCS) ──────────────────────────
            // Четыре угла таблицы в мировых координатах.
            var p1 = LW(tblLocalX,          tblLocalY,          cA, sA); // верхний левый
            var p2 = LW(tblLocalX + tableW, tblLocalY,          cA, sA); // верхний правый
            var p3 = LW(tblLocalX + tableW, tblLocalY - tableH, cA, sA); // нижний правый
            var p4 = LW(tblLocalX,          tblLocalY - tableH, cA, sA); // нижний левый

            // Wipeout.SetFrom требует ЯВНО ЗАМКНУТЫЙ контур: первая точка
            // повторяется в конце, иначе внутренний расчёт bbox даёт
            // гигантскую маскировку на весь чертёж.
            var pts = new Point2dCollection();
            pts.Add(new Point2d(p1.X, p1.Y));
            pts.Add(new Point2d(p2.X, p2.Y));
            pts.Add(new Point2d(p3.X, p3.Y));
            pts.Add(new Point2d(p4.X, p4.Y));
            pts.Add(new Point2d(p1.X, p1.Y));

            var wo = new Wipeout();
            wo.SetDatabaseDefaults();
            wo.Layer = _o.TableLayerName;
            wo.SetFrom(pts, Vector3d.ZAxis);

            // ── Анонимный блок «маскировка + таблица» ──────────────────────────
            // Имя "*U" → AutoCAD сам присваивает уникальное анонимное имя.
            var bt  = (BlockTable)t.GetObject(_db.BlockTableId, OpenMode.ForWrite);
            var btr = new BlockTableRecord { Name = "*U" };
            var btrId = bt.Add(btr);
            t.AddNewlyCreatedDBObject(btr, true);

            // Порядок добавления = порядок прорисовки внутри блока:
            //   маскировка снизу, таблица сверху.
            btr.AppendEntity(wo);
            t.AddNewlyCreatedDBObject(wo, true);
            btr.AppendEntity(tbl);
            t.AddNewlyCreatedDBObject(tbl, true);
            SaveSummaryData(t, tbl);

            // Геометрия уже в WCS → вставляем блок в начало координат без смещения.
            var br = new BlockReference(Point3d.Origin, btrId);
            br.SetDatabaseDefaults();
            br.Layer = _o.TableLayerName;
            _ms.AppendEntity(br);
            t.AddNewlyCreatedDBObject(br, true);

            // Поднимаем весь блок поверх существующего чертежа.
            var dot = (DrawOrderTable)t.GetObject(
                _ms.DrawOrderTableId, OpenMode.ForWrite);
            var brIds = new ObjectIdCollection { br.ObjectId };
            dot.MoveToTop(brIds);
        }

        // ── Данные итоговой таблицы внутри неё самой (словарь расширений) ────────
        private const string SummaryDataKey     = "OPENKARTOGRAMMA_SUMMARY";
        // v1: 10 полей заголовка; v2: + площади выемки/насыпи и флаги их строк.
        private const double SummaryDataVersion = 2;
        private static int SummaryHeaderLength(double version) => version == 1 ? 10 : 14;
        private double[]? _summaryData;

        private void SaveSummaryData(Transaction t,
            Autodesk.AutoCAD.DatabaseServices.Table tbl)
        {
            if (_summaryData == null) return;
            tbl.CreateExtensionDictionary();
            var dict = (DBDictionary)t.GetObject(tbl.ExtensionDictionary, OpenMode.ForWrite);
            var rb = new ResultBuffer();
            foreach (double v in _summaryData)
                rb.Add(new TypedValue((int)DxfCode.Real, v));
            var xr = new Xrecord { Data = rb };
            dict.SetAt(SummaryDataKey, xr);
            t.AddNewlyCreatedDBObject(xr, true);
        }

        private static double[]? LoadSummaryData(Transaction t,
            Autodesk.AutoCAD.DatabaseServices.Table tbl)
        {
            if (tbl.ExtensionDictionary.IsNull) return null;
            var dict = (DBDictionary)t.GetObject(tbl.ExtensionDictionary, OpenMode.ForRead);
            if (!dict.Contains(SummaryDataKey)) return null;
            var xr = (Xrecord)t.GetObject(dict.GetAt(SummaryDataKey), OpenMode.ForRead);
            if (xr.Data == null) return null;
            var vals = xr.Data.AsArray().Select(tv => Convert.ToDouble(tv.Value)).ToArray();
            if (vals.Length < 10 || (vals[0] != 1 && vals[0] != SummaryDataVersion)) return null;
            int head = SummaryHeaderLength(vals[0]);
            int rows = (int)vals[1], cols = (int)vals[2];
            return vals.Length == head + 2 * cols + 2 * rows ? vals : null;
        }

        /// <summary>
        /// Перестраивает итоговые таблицы под текущие шрифт, стиль и цвет
        /// таблицы. Одной лишь сменой высоты текста не обойтись: ячейки
        /// заголовков и итогов свёрстаны «впритык» к тексту, а сама таблица
        /// сдвинута так, чтобы строки/колонки данных совпали с рядами сетки.
        /// Поэтому таблица (с маскировкой, если она есть) и подпись итогов
        /// удаляются и строятся заново тем же DrawSummaryTable по данным,
        /// сохранённым при построении. Таблицы без данных (построены прошлыми
        /// версиями) только получают новую высоту, цвет и стиль, как раньше.
        /// Возвращает число перестроенных таблиц.
        /// </summary>
        private int RebuildSummaryTables()
        {
            using var trans = _db.TransactionManager.StartTransaction();
            var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
            _ms = (BlockTableRecord)trans.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var found  = new List<(ObjectId Erase, double[] Data)>();
            var labels = new List<ObjectId>();
            foreach (ObjectId id in _ms)
            {
                var ent = trans.GetObject(id, OpenMode.ForRead) as AcadEntity;
                if (ent == null || ent.Layer != _o.TableLayerName) continue;

                if (ent is Autodesk.AutoCAD.DatabaseServices.Table tbl)
                {
                    var d = LoadSummaryData(trans, tbl);
                    if (d != null) found.Add((id, d));
                }
                else if (ent is BlockReference br)
                {
                    // Таблица с маскировкой — в анонимном блоке (см. PlaceTable)
                    var btr = (BlockTableRecord)trans.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    foreach (ObjectId sub in btr)
                        if (trans.GetObject(sub, OpenMode.ForRead) is
                                Autodesk.AutoCAD.DatabaseServices.Table inner)
                        {
                            var d = LoadSummaryData(trans, inner);
                            if (d != null) found.Add((id, d));
                        }
                }
                // «Площадь:», «Площадь выемки:», «Площадь насыпи:»
                else if (ent is MText mt && mt.Text.StartsWith("Площадь"))
                    labels.Add(id);
            }
            if (found.Count == 0) { trans.Commit(); return 0; }

            foreach (var id in labels) trans.GetObject(id, OpenMode.ForWrite).Erase();

            var tst = (TextStyleTable)trans.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
            _tblTsId = tst.Has(_o.TableTextStyleName) ? tst[_o.TableTextStyleName] : _db.Textstyle;
            double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);
            double baseX = _o.BaseX, baseY = _o.BaseY;

            foreach (var (eraseId, d) in found)
            {
                trans.GetObject(eraseId, OpenMode.ForWrite).Erase();

                int rows = (int)d[1], cols = (int)d[2];
                bool v2 = d[0] == 2;
                int k = SummaryHeaderLength(d[0]);
                double[] Take(int n) { var a = new double[n]; Array.Copy(d, k, a, 0, n); k += n; return a; }
                var colCut  = Take(cols);
                var colFill = Take(cols);
                var rowCut  = Take(rows);
                var rowFill = Take(rows);

                // Базовая точка — та, от которой таблица строилась (при
                // «авто» она вычисляется только в расчёте объёмов).
                _o.BaseX = d[8]; _o.BaseY = d[9];
                DrawSummaryTable(trans, rows, cols, cA, sA,
                    colCut, colFill, rowCut, rowFill, d[5], d[6],
                    (int)d[3], (int)d[4], d[7],
                    v2 ? d[10] : 0, v2 ? d[11] : 0,
                    v2 && d[12] != 0, v2 && d[13] != 0);
            }
            _o.BaseX = baseX; _o.BaseY = baseY;

            trans.Commit();
            return found.Count;
        }

// Вспомогательный метод заполнения ячейки таблицы
        private static void SetCell(Autodesk.AutoCAD.DatabaseServices.Table tbl, int row, int col, string text,
            double textHeight, CellAlignment align, int aci)
        {
            var cell = tbl.Cells[row, col];
            cell.TextString  = text;
            cell.TextHeight  = textHeight;
            cell.Alignment   = align;
            cell.ContentColor = Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Служебные методы
        // ═══════════════════════════════════════════════════════════════════════

        private static double? GetElev(CivilSurface surf, double x, double y)
        {
            try
            {
                return surf switch
                {
                    TinSurface  tin => tin.FindElevationAtXY(x, y),
                    GridSurface grd => grd.FindElevationAtXY(x, y),
                    _               => null
                };
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Быстрые отметки: снимок TIN в памяти вместо COM-вызова FindElevationAtXY
        //  на каждый субузел (их сотни тысяч за расчёт; каждый промах у границы —
        //  ещё и дорогое .NET-исключение). Снимки строятся один раз за операцию в
        //  PrepareElevationCache; GetElevS прозрачно падает обратно на GetElev для
        //  поверхностей без снимка (GridSurface и т.п.).
        // ═══════════════════════════════════════════════════════════════════════
        private TinSnapshot?  _snap1,     _snap2;
        private CivilSurface? _snapSurf1, _snapSurf2;

        private void PrepareElevationCache(CivilSurface s1, CivilSurface s2)
        {
            // Аварийный выключатель для диагностики: KARTOGRAMMA_NOCACHE=1 →
            // все отметки идут через Civil API, как до ускорения. Позволяет
            // проверить, влияет ли кеш на результат, не пересобирая плагин.
            if (Environment.GetEnvironmentVariable("KARTOGRAMMA_NOCACHE") == "1")
            {
                _snap1 = _snap2 = null;
                _snapSurf1 = _snapSurf2 = null;
                _doc.Editor.WriteMessage(
                    "\n[Картограмма] Кеш поверхностей ОТКЛЮЧЁН (KARTOGRAMMA_NOCACHE=1) — отметки через Civil API.");
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _snap1 = s1 is TinSurface t1 ? VerifySnapshot(s1, TinSnapshot.Build(t1)) : null;
            _snap2 = s2 is TinSurface t2 ? VerifySnapshot(s2, TinSnapshot.Build(t2)) : null;
            _snapSurf1 = _snap1 != null ? s1 : null;
            _snapSurf2 = _snap2 != null ? s2 : null;
            if (_snap1 != null || _snap2 != null)
                _doc.Editor.WriteMessage(
                    $"\n[Картограмма] Кеш поверхностей: {_snap1?.TriangleCount ?? 0} + " +
                    $"{_snap2?.TriangleCount ?? 0} треугольников за {sw.ElapsedMilliseconds} мс");
        }

        /// <summary>
        /// Самопроверка снимка TIN: сетка контрольных точек по габаритам
        /// поверхности сравнивается с FindElevationAtXY. Любое расхождение
        /// (есть/нет данных или |Δz| &gt; 1 мм) означает семантику, которую
        /// снимок не воспроизвёл (например, экзотические границы поверхности) —
        /// тогда снимок отбрасывается и отметки идут через Civil API:
        /// медленнее, но гарантированно совпадает с Civil.
        /// </summary>
        private TinSnapshot? VerifySnapshot(CivilSurface s, TinSnapshot? snap)
        {
            if (snap == null) return null;
            try
            {
                // 40×40 = 1600 контрольных точек: достаточно плотно, чтобы
                // зацепить даже маленькие локальные зоны (зумпф, приямок) —
                // при этом разовая стоимость ~тысячи API-вызовов незаметна.
                var e = s.GeometricExtents;
                const int N = 40;
                double stX = (e.MaxPoint.X - e.MinPoint.X) / N;
                double stY = (e.MaxPoint.Y - e.MinPoint.Y) / N;
                int bad = 0;
                for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    // Полуотступ — не попадать ровно на кромки/углы поверхности
                    double x = e.MinPoint.X + (j + 0.5) * stX;
                    double y = e.MinPoint.Y + (i + 0.5) * stY;
                    double? zApi  = GetElev(s, x, y);
                    double? zSnap = snap.Elevation(x, y);
                    if (zApi.HasValue != zSnap.HasValue) { bad++; continue; }
                    if (zApi.HasValue && Math.Abs(zApi.Value - zSnap!.Value) > 1e-3) bad++;
                }
                if (bad == 0) return snap;

                _doc.Editor.WriteMessage(
                    $"\n[Картограмма] Кеш '{s.Name}': расхождение с Civil API в {bad}/{N * N} " +
                    "контрольных точек — кеш отключён, отметки считаются через API (медленнее, но точно).");
                return null;
            }
            catch { return snap; }
        }

        /// <summary>Отметка поверхности: из снимка (быстро, потокобезопасно),
        /// для поверхностей без снимка — обычный GetElev через Civil API.</summary>
        private double? GetElevS(CivilSurface surf, double x, double y)
        {
            if (ReferenceEquals(surf, _snapSurf1)) return _snap1!.Elevation(x, y);
            if (ReferenceEquals(surf, _snapSurf2)) return _snap2!.Elevation(x, y);
            return GetElev(surf, x, y);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Зоны резких перепадов (зумпф, приямок, локальная насыпь).
        //  Рабочие отметки сканируются по всей сетке; компактные области, где
        //  |h − медиана| резко выше остального поля (AnomalyDetector), выделяются
        //  ОТДЕЛЬНЫМИ квадратами: объём зоны считается и подписывается отдельно,
        //  а из обычных ячеек вырезается — картограмма сразу показывает, сколько
        //  грунта на траншею/котлован и сколько на разработку под зумпф.
        //  Прямоугольники зон — в ЛОКАЛЬНЫХ координатах сетки.
        // ═══════════════════════════════════════════════════════════════════════
        private List<(double x0, double y0, double x1, double y1)>? _anomalyZones;
        private double _zoneCos = 1.0, _zoneSin;

        private void DetectAnomalyZones(CivilSurface s1, CivilSurface s2,
            int rows, int cols, double cosA, double sinA)
        {
            _anomalyZones = null;
            _zoneCos = cosA; _zoneSin = sinA;

            double W = cols * _o.CellSizeX, H = rows * _o.CellSizeY;
            if (W <= 0 || H <= 0) return;

            // Шаг сэмплирования: достаточно мелкий, чтобы увидеть зумпф ~1×1 м.
            // Без кеша поверхностей — грубее и с жёстким потолком числа вызовов.
            bool fast = _snap1 != null && _snap2 != null;
            double st = Math.Min(_o.CellSizeX, _o.CellSizeY) / (fast ? 40.0 : 10.0);
            long cap = fast ? 400_000 : 25_000;
            while ((long)(W / st + 1) * (long)(H / st + 1) > cap) st *= 1.5;

            int nj = Math.Max(4, (int)Math.Round(W / st));
            int ni = Math.Max(4, (int)Math.Round(H / st));
            double stX = W / nj, stY = H / ni;

            var h = new double[ni, nj];
            for (int i = 0; i < ni; i++)
            for (int j = 0; j < nj; j++)
            {
                double lx = (j + 0.5) * stX, ly = (i + 0.5) * stY;
                double wx = _o.BaseX + lx * cosA - ly * sinA;
                double wy = _o.BaseY + lx * sinA + ly * cosA;
                double? e1 = GetElevS(s1, wx, wy);
                double? e2 = GetElevS(s2, wx, wy);
                h[i, j] = (e1.HasValue && e2.HasValue && IsInClipRegion(wx, wy))
                    ? e2.Value - e1.Value : double.NaN;
            }

            // Локальный перепад не может быть крупнее ~2 ячеек — иначе это рельеф
            int maxSide = (int)Math.Ceiling(
                2.0 * Math.Max(_o.CellSizeX, _o.CellSizeY) / Math.Min(stX, stY));

            var zones = AnomalyDetector.FindZones(h, minDev: 1.0, minCount: 4,
                maxSide: maxSide, maxZones: 10, peakFactor: 2.5);
            if (zones.Count == 0) return;

            // Индексные bbox → локальные прямоугольники с запасом в 2 сэмпла
            // (захватываем стенку перепада, оставшуюся ниже порога)
            var rects = new List<(double x0, double y0, double x1, double y1)>();
            foreach (var z in zones)
            {
                double x0 = Math.Max(0, (z.j0 - 2) * stX);
                double x1 = Math.Min(W, (z.j1 + 3) * stX);
                double y0 = Math.Max(0, (z.i0 - 2) * stY);
                double y1 = Math.Min(H, (z.i1 + 3) * stY);
                rects.Add((x0, y0, x1, y1));
            }

            // Слить пересекающиеся прямоугольники (после расширения зоны могли слипнуться)
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int a = 0; a < rects.Count && !merged; a++)
                for (int b = a + 1; b < rects.Count && !merged; b++)
                {
                    var ra = rects[a]; var rb = rects[b];
                    bool overlap = ra.x0 <= rb.x1 && rb.x0 <= ra.x1 &&
                                   ra.y0 <= rb.y1 && rb.y0 <= ra.y1;
                    if (!overlap) continue;
                    rects[a] = (Math.Min(ra.x0, rb.x0), Math.Min(ra.y0, rb.y0),
                                Math.Max(ra.x1, rb.x1), Math.Max(ra.y1, rb.y1));
                    rects.RemoveAt(b);
                    merged = true;
                }
            }

            _anomalyZones = rects;
            for (int k = 0; k < rects.Count; k++)
            {
                var z = rects[k];
                _doc.Editor.WriteMessage(
                    $"\n[Картограмма] Резкий перепад #{k + 1}: зона {z.x1 - z.x0:F1}×{z.y1 - z.y0:F1} м — выделена отдельной ячейкой.");
            }
        }

        /// <summary>Точка (мировая) внутри одной из зон резкого перепада —
        /// такие точки исключаются из объёма обычных ячеек (объём зоны
        /// считается и подписывается отдельно).</summary>
        private bool InAnomalyZoneW(double wx, double wy)
        {
            var zs = _anomalyZones;
            if (zs == null) return false;
            double dx = wx - _o.BaseX, dy = wy - _o.BaseY;
            double lx = dx * _zoneCos + dy * _zoneSin;
            double ly = -dx * _zoneSin + dy * _zoneCos;
            for (int k = 0; k < zs.Count; k++)
            {
                var z = zs[k];
                if (lx >= z.x0 && lx <= z.x1 && ly >= z.y0 && ly <= z.y1) return true;
            }
            return false;
        }

        /// <summary>Объём и площадь перекрытия внутри зоны перепада
        /// (midpoint-интегрирование с шагом субсетки объёмов).</summary>
        private void CalcZoneVolume((double x0, double y0, double x1, double y1) z,
            CivilSurface s1, CivilSurface s2, double cosA, double sinA,
            out double vol, out double area, out double areaFill, out double areaCut)
        {
            vol = 0; area = 0; areaFill = 0; areaCut = 0;
            double w = z.x1 - z.x0, ht = z.y1 - z.y0;
            if (w <= 0 || ht <= 0) return;

            int n = Clamp((int)Math.Ceiling(
                Math.Max(w, ht) / Math.Max(_o.VolumeNodeStep, 1e-6)), 8, 400);
            double dx = w / n, dy = ht / n, dA = dx * dy;

            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double lx = z.x0 + (j + 0.5) * dx;
                double ly = z.y0 + (i + 0.5) * dy;
                double wx = _o.BaseX + lx * cosA - ly * sinA;
                double wy = _o.BaseY + lx * sinA + ly * cosA;
                double? e1 = GetElevS(s1, wx, wy);
                double? e2 = GetElevS(s2, wx, wy);
                if (!e1.HasValue || !e2.HasValue || !IsInBounds(wx, wy)) continue;
                double hw = e2.Value - e1.Value;
                vol  += hw * dA;
                area += dA;
                if (hw > 0) areaFill += dA; else if (hw < 0) areaCut += dA;
            }
        }

        /// <summary>Нарисовать рамку зоны перепада на слое сетки.</summary>
        private void DrawZoneRect(Transaction t,
            (double x0, double y0, double x1, double y1) z, double cosA, double sinA)
        {
            var pl = new Polyline();
            pl.AddVertexAt(0, ToUcs2d(LW(z.x0, z.y0, cosA, sinA)), 0, 0, 0);
            pl.AddVertexAt(1, ToUcs2d(LW(z.x1, z.y0, cosA, sinA)), 0, 0, 0);
            pl.AddVertexAt(2, ToUcs2d(LW(z.x1, z.y1, cosA, sinA)), 0, 0, 0);
            pl.AddVertexAt(3, ToUcs2d(LW(z.x0, z.y1, cosA, sinA)), 0, 0, 0);
            pl.Closed = true;
            pl.Layer  = _o.GridLayerName;
            _ms.AppendEntity(pl);
            t.AddNewlyCreatedDBObject(pl, true);
        }

        private void EnsureLayer(Transaction trans, string name, int aci)
        {
            var lt = (LayerTable)trans.GetObject(_db.LayerTableId, OpenMode.ForWrite);
            if (lt.Has(name)) return;
            var ltr = new LayerTableRecord
            {
                Name  = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, (short)aci)
            };
            lt.Add(ltr);
            trans.AddNewlyCreatedDBObject(ltr, true);
        }

        private static int EraseByLayer(Transaction t, BlockTableRecord ms, string layerName,
            Func<AcadEntity, bool>? where = null)
        {
            var ids = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                try
                {
                    if (t.GetObject(id, OpenMode.ForRead) is AcadEntity ent &&
                        string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase) &&
                        (where == null || where(ent)))
                        ids.Add(id);
                }
                catch { }
            }
            foreach (var id in ids)
            {
                try { ((AcadEntity)t.GetObject(id, OpenMode.ForWrite)).Erase(); } catch { }
            }
            return ids.Count;
        }

        // Локальные → мировые координаты
        private Point3d LW(double lx, double ly, double cosA, double sinA) =>
            new Point3d(
                _o.BaseX + lx * cosA - ly * sinA,
                _o.BaseY + lx * sinA + ly * cosA,
                0);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        // Угол текста — в МИРОВЫХ осях, независимо от текущей ПСК (у MText
        // свойство Rotation считается от оси X текущей ПСК).
        private static Vector3d TextDir(double angle) =>
            new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);

        private static double TextAngle(MText mt) =>
            Math.Atan2(mt.Direction.Y, mt.Direction.X);

        private MText AddTextToLayer(Transaction t, string layer, Point3d pos, string text,
            double h, double angle, int aci, ObjectId styleId = default, bool hideMask = false)
        {
            var mt = new MText
            {
                Location        = pos,
                Contents        = text,
                TextHeight      = h,
                // Направление — мировым вектором: MText.Rotation
                // отсчитывается от оси X ТЕКУЩЕЙ ПСК, и при повёрнутой
                // ПСК текст уходил бы с угла сетки.
                Direction       = TextDir(angle),
                Layer           = layer,
                Color           = Color.FromColorIndex(ColorMethod.ByAci, (short)aci),
                TextStyleId     = styleId.IsNull ? _tsId : styleId,
                Attachment      = AttachmentPoint.BottomLeft,
                Width           = 0
            };
            if (hideMask)
            {
                mt.BackgroundFill        = true;
                mt.BackgroundScaleFactor = 1.1;
            }
            _ms.AppendEntity(mt);
            t.AddNewlyCreatedDBObject(mt, true);
            return mt;
        }

        // Подпись итогов у таблицы: каждая строка — отдельный MText.
        // bottomLocalY — низ ПОСЛЕДНЕЙ строки (локальные координаты сетки),
        // строки выше неё идут через lineGap.
        private void AddSummaryLines(Transaction t, double localX, double bottomLocalY,
            List<string> lines, double h, double angle, int aci, double lineGap,
            double cA, double sA)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                double ly = bottomLocalY + (lines.Count - 1 - i) * lineGap;
                AddTextToLayer(t, _o.TableLayerName, LW(localX, ly, cA, sA), lines[i],
                    h, angle, aci, _tblTsId, hideMask: _o.HideMaskTable);
            }
        }

        private MText AddRightAlignedText(Transaction t, string layer, Point3d rightEdge, string text,
            double h, double angle, int aci, bool hideMask = false)
        {
            var mt = new MText
            {
                Location        = rightEdge,
                Contents        = text,
                TextHeight      = h,
                // Направление — мировым вектором: MText.Rotation
                // отсчитывается от оси X ТЕКУЩЕЙ ПСК, и при повёрнутой
                // ПСК текст уходил бы с угла сетки.
                Direction       = TextDir(angle),
                Layer           = layer,
                Color           = Color.FromColorIndex(ColorMethod.ByAci, (short)aci),
                TextStyleId     = _tsId,
                Attachment      = AttachmentPoint.BottomRight,
                Width           = 0
            };
            if (hideMask)
            {
                mt.BackgroundFill        = true;
                mt.BackgroundScaleFactor = 1.1;
            }
            _ms.AppendEntity(mt);
            t.AddNewlyCreatedDBObject(mt, true);
            return mt;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Подписи объёмов без наездов на тройки отметок.
        //  Каждая нарисованная подпись (тройки узлов и пересечений с границей,
        //  уже поставленные объёмы) запоминает свой прямоугольник в локальных
        //  координатах сетки — по ФАКТИЧЕСКИМ габаритам MText (ActualWidth /
        //  ActualHeight), поэтому работает при любом шрифте и высоте текста.
        //  Объём ставится в ближайшую к своему центру (центр ячейки или центр
        //  своей части — насыпи/выемки) точку, где он ни с чем не пересекается.
        // ═══════════════════════════════════════════════════════════════════════
        private readonly List<(double X0, double Y0, double X1, double Y1)> _labelBoxes = new();

        private (double X0, double Y0, double X1, double Y1) LocalBox(MText mt)
        {
            double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);
            double dx = mt.Location.X - _o.BaseX, dy = mt.Location.Y - _o.BaseY;
            double lx =  dx * cA + dy * sA;
            double ly = -dx * sA + dy * cA;
            double w = mt.ActualWidth, h = mt.ActualHeight;
            if (!(w > 0)) w = mt.Contents.Length * mt.TextHeight * 0.7;
            if (!(h > 0)) h = mt.TextHeight;
            return mt.Attachment switch
            {
                AttachmentPoint.BottomRight  => (lx - w, ly, lx, ly + h),
                AttachmentPoint.MiddleCenter => (lx - w / 2, ly - h / 2, lx + w / 2, ly + h / 2),
                _                            => (lx, ly, lx + w, ly + h),   // BottomLeft
            };
        }

        private MText RecordLabelBox(MText mt)
        {
            _labelBoxes.Add(LocalBox(mt));
            return mt;
        }

        /// <summary>
        /// Сдвинуть подпись объёма в ближайшее к (idealLx, idealLy) место внутри
        /// прямоугольника области (ячейка или зона перепада), где она не
        /// перекрывает уже стоящие подписи. inPart — точка годится под центр
        /// подписи (внутри своей части ячейки, в пределах границ). Если
        /// свободного места нет — место с наименьшим перекрытием.
        /// </summary>
        private void PlaceVolumeLabel(MText mt, double bx0, double by0, double bw, double bh,
            double idealLx, double idealLy, Func<double, double, bool> inPart, VolLabelKind kind)
        {
            var box  = LocalBox(mt);
            double w = box.X1 - box.X0, h = box.Y1 - box.Y0;
            var (bestX, bestY) = FindLabelSpot(w, h, h * 0.15,
                bx0, by0, bw, bh, idealLx, idealLy, inPart);

            double cAng = Math.Cos(_o.RotationRadians), sAng = Math.Sin(_o.RotationRadians);
            mt.Location = LW(bestX, bestY, cAng, sAng);

            // Всё, что нужно «Перерисовать», чтобы подобрать место заново под
            // новый шрифт (RelayoutVolumeLabels): область, центр, часть и
            // место, куда поставили, — по нему видно, двигал ли её пользователь.
            mt.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, VolPosAppName),
                new TypedValue((int)DxfCode.ExtendedDataReal, _o.BaseX),
                new TypedValue((int)DxfCode.ExtendedDataReal, _o.BaseY),
                new TypedValue((int)DxfCode.ExtendedDataReal, _o.RotationDegrees),
                new TypedValue((int)DxfCode.ExtendedDataReal, bx0),
                new TypedValue((int)DxfCode.ExtendedDataReal, by0),
                new TypedValue((int)DxfCode.ExtendedDataReal, bw),
                new TypedValue((int)DxfCode.ExtendedDataReal, bh),
                new TypedValue((int)DxfCode.ExtendedDataReal, idealLx),
                new TypedValue((int)DxfCode.ExtendedDataReal, idealLy),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)kind),
                new TypedValue((int)DxfCode.ExtendedDataReal, bestX),
                new TypedValue((int)DxfCode.ExtendedDataReal, bestY));
        }

        /// <summary>
        /// Ближайшее к (idealLx, idealLy) место под прямоугольник w×h (с полем
        /// pad) в области, где он не перекрывает уже стоящие подписи. Занятое
        /// место запоминается в _labelBoxes. Координаты — локальные, сетки.
        /// </summary>
        private (double X, double Y) FindLabelSpot(double w, double h, double pad,
            double bx0, double by0, double bw, double bh,
            double idealLx, double idealLy, Func<double, double, bool> inPart)
        {
            double hw = w / 2 + pad, hh = h / 2 + pad;

            // Только подписи рядом с областью — остальные заведомо не мешают.
            var near = _labelBoxes.Where(b =>
                b.X1 > bx0 - w && b.X0 < bx0 + bw + w &&
                b.Y1 > by0 - h && b.Y0 < by0 + bh + h).ToList();

            double Overlap(double cx, double cy)
            {
                double s = 0;
                foreach (var b in near)
                {
                    double ox = Math.Min(cx + hw, b.X1) - Math.Max(cx - hw, b.X0);
                    double oy = Math.Min(cy + hh, b.Y1) - Math.Max(cy - hh, b.Y0);
                    if (ox > 0 && oy > 0) s += ox * oy;
                }
                return s;
            }

            double bestX = idealLx, bestY = idealLy;
            if (Overlap(idealLx, idealLy) > 0)
            {
                // Кандидаты — сетка по области; подпись целиком внутри области,
                // если влезает, иначе её центр в середине по этой оси.
                double step = Math.Max(Math.Min(bw, bh) / 60.0, h * 0.1);
                double xLo = bx0 + hw, xHi = bx0 + bw - hw;
                double yLo = by0 + hh, yHi = by0 + bh - hh;
                if (xLo > xHi) xLo = xHi = bx0 + bw / 2;
                if (yLo > yHi) yLo = yHi = by0 + bh / 2;

                var cand = new List<(double X, double Y, double D2)>();
                for (double y = yLo; y <= yHi + 1e-9; y += step)
                for (double x = xLo; x <= xHi + 1e-9; x += step)
                {
                    double dx = x - idealLx, dy = y - idealLy;
                    cand.Add((x, y, dx * dx + dy * dy));
                }
                cand.Sort((a, b) => a.D2.CompareTo(b.D2));

                double bestScore = double.MaxValue;
                bool found = false;
                foreach (var (x, y, d2) in cand)
                {
                    double ov = Overlap(x, y);
                    if (ov <= 0)
                    {
                        // Ближайшее свободное место — если оно в своей части
                        if (!inPart(x, y)) continue;
                        bestX = x; bestY = y; found = true;
                        break;
                    }
                    // Запасной вариант: наименьшее перекрытие, затем близость
                    double score = ov * 1e6 + d2;
                    if (score < bestScore && inPart(x, y))
                    { bestScore = score; bestX = x; bestY = y; }
                }
                if (!found && bestScore == double.MaxValue) { bestX = idealLx; bestY = idealLy; }
            }

            _labelBoxes.Add((bestX - w / 2, bestY - h / 2, bestX + w / 2, bestY + h / 2));
            return (bestX, bestY);
        }

        /// <summary>
        /// «Площадь и объём»: круг, разделённый горизонтальной полилинией
        /// пополам; в середине верхней половины — объём, нижней — площадь.
        /// Радиус подбирается по фактическим габаритам цифр (шрифт, высота,
        /// число знаков), чтобы обе подписи целиком вписывались в свои
        /// половины. Всё — на слое объёмов. Место под круг ищется так же,
        /// как под обычную подпись объёма, — без наездов на тройки отметок.
        /// </summary>
        private void PlaceVolumeAreaLabel(Transaction t, string volText, string areaText,
            double vh, double angle, int aci,
            double bx0, double by0, double bw, double bh,
            double idealLx, double idealLy, Func<double, double, bool> inPart)
        {
            double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);
            var ideal = LW(idealLx, idealLy, cA, sA);
            var mtV = AddCenteredText(t, _o.VolumeLayerName, ideal, volText, vh, angle, aci,
                hideMask: _o.HideMaskVolume);
            var mtS = AddCenteredText(t, _o.VolumeLayerName, ideal, areaText, vh, angle, aci,
                hideMask: _o.HideMaskVolume);
            var bV = LocalBox(mtV);
            var bS = LocalBox(mtS);

            // Полуширина и высота более крупной из двух подписей
            double a  = Math.Max(bV.X1 - bV.X0, bS.X1 - bS.X0) / 2;
            double th = Math.Max(bV.Y1 - bV.Y0, bS.Y1 - bS.Y0);
            // Зазор цифр от черты; дальние углы цифр + запас — на окружности
            double gap = th * 0.3;
            double off = gap + th / 2;                    // центр цифр от черты
            double r   = Math.Sqrt(a * a + (gap + th) * (gap + th)) + th * 0.25;

            var (lx, ly) = FindLabelSpot(2 * r, 2 * r, th * 0.15,
                bx0, by0, bw, bh, idealLx, idealLy, inPart);

            mtV.Location = LW(lx, ly + off, cA, sA);
            mtS.Location = LW(lx, ly - off, cA, sA);

            var center = LW(lx, ly, cA, sA);
            var circle = new Circle
            {
                Center = center,
                Radius = r,
                Normal = Vector3d.ZAxis,
                Layer  = _o.VolumeLayerName,
                Color  = Color.FromColorIndex(ColorMethod.ByAci, (short)aci),
            };
            _ms.AppendEntity(circle);
            t.AddNewlyCreatedDBObject(circle, true);

            // Черта — по диаметру, вдоль оси сетки
            var p0 = LW(lx - r, ly, cA, sA);
            var p1 = LW(lx + r, ly, cA, sA);
            var pl = new Polyline();
            pl.SetDatabaseDefaults();
            pl.AddVertexAt(0, new Point2d(p0.X, p0.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(p1.X, p1.Y), 0, 0, 0);
            pl.Elevation = center.Z;
            pl.Layer = _o.VolumeLayerName;
            pl.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
            _ms.AppendEntity(pl);
            t.AddNewlyCreatedDBObject(pl, true);

            // Связываем в группу: объём — главный, у остальных ссылка на него.
            // По любой части выноска переносит круг целиком.
            mtV.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, VolAreaAppName),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)0),
                new TypedValue((int)DxfCode.ExtendedDataHandle, mtS.Handle.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataHandle, circle.Handle.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataHandle, pl.Handle.ToString()));
            foreach (var member in new AcadEntity[] { mtS, circle, pl })
                member.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, VolAreaAppName),
                    new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1),
                    new TypedValue((int)DxfCode.ExtendedDataHandle, mtV.Handle.ToString()));
        }

        // XData группы «Площадь и объём». Главный (MText объёма):
        // [0, площадь, круг, черта]; остальные части: [1, объём].
        private const string VolAreaAppName = "OPENKARTOGRAMMA_VOLAREA";

        private ObjectId IdFromHandle(string hex)
        {
            try
            {
                var id = _db.GetObjectId(false, new Handle(Convert.ToInt64(hex, 16)), 0);
                return id.IsNull || id.IsErased ? ObjectId.Null : id;
            }
            catch { return ObjectId.Null; }
        }

        /// <summary>Главный объект (MText объёма) группы «Площадь и объём»,
        /// в которую входит obj; Null — obj не из такой группы.</summary>
        private ObjectId VolAreaMaster(Autodesk.AutoCAD.DatabaseServices.DBObject obj)
        {
            var rb = obj.GetXDataForApplication(VolAreaAppName);
            if (rb == null) return ObjectId.Null;
            short role = -1;
            foreach (TypedValue tv in rb)
            {
                if (tv.TypeCode == (int)DxfCode.ExtendedDataInteger16) role = (short)tv.Value;
                else if (tv.TypeCode == (int)DxfCode.ExtendedDataHandle && role == 1)
                    return IdFromHandle((string)tv.Value);
            }
            return role == 0 ? obj.ObjectId : ObjectId.Null;
        }

        /// <summary>Части группы по главному MText объёма (открыты на чтение).</summary>
        private bool TryGetVolAreaGroup(Transaction t, MText master,
            out MText area, out Circle circle, out Polyline divider)
        {
            area = null!; circle = null!; divider = null!;
            var rb = master.GetXDataForApplication(VolAreaAppName);
            if (rb == null) return false;
            var ids = new List<ObjectId>();
            foreach (TypedValue tv in rb)
                if (tv.TypeCode == (int)DxfCode.ExtendedDataHandle)
                    ids.Add(IdFromHandle((string)tv.Value));
            if (ids.Count != 3 || ids.Any(id => id.IsNull)) return false;
            if (t.GetObject(ids[0], OpenMode.ForRead) is not MText a ||
                t.GetObject(ids[1], OpenMode.ForRead) is not Circle c ||
                t.GetObject(ids[2], OpenMode.ForRead) is not Polyline d) return false;
            area = a; circle = c; divider = d;
            return true;
        }

        /// <summary>
        /// Выноска круга «Площадь и объём»: от исходного места к ближнему
        /// концу черты круга — как у одиночного объёма к концу черты под
        /// цифрой. Если круг вернули на исходное место — выноска не нужна.
        /// </summary>
        private ObjectId DrawVolumeAreaLeader(Transaction t, BlockTableRecord ms,
            Circle circle, Polyline divider, Point3d anchor)
        {
            var c = circle.Center;
            if (new Point2d(anchor.X, anchor.Y).GetDistanceTo(new Point2d(c.X, c.Y)) <= circle.Radius)
                return ObjectId.Null;
            var e0 = divider.GetPoint2dAt(0);
            var e1 = divider.GetPoint2dAt(1);
            var a2 = new Point2d(anchor.X, anchor.Y);
            var end = a2.GetDistanceTo(e0) <= a2.GetDistanceTo(e1) ? e0 : e1;

            var pl = new Polyline();
            pl.SetDatabaseDefaults();
            pl.AddVertexAt(0, a2, 0, 0, 0);
            pl.AddVertexAt(1, end, 0, 0, 0);
            pl.Elevation = c.Z;
            pl.Layer = _o.VolumeLayerName;
            pl.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)_o.ColorVolume);
            ms.AppendEntity(pl);
            t.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }

        /// <summary>Площадь для подписи в круге — с точностью объёмов, без знака.</summary>
        private string Area(double a) => Num(Math.Abs(a), _o.VolumePrecision);

        internal enum VolLabelKind : short { Cell = 0, Fill = 1, Cut = 2 }

        // XData подписи объёма с данными для повторной раскладки. Отдельное
        // приложение — у перенесённых подписей своя XData (XAppName).
        private const string VolPosAppName = "OPENKARTOGRAMMA_VOLPOS";

        private void EnsureVolPosRegApp(Transaction t)
        {
            var rat = (RegAppTable)t.GetObject(_db.RegAppTableId, OpenMode.ForRead);
            foreach (var name in new[] { VolPosAppName, VolAreaAppName })
            {
                if (rat.Has(name)) continue;
                if (!rat.IsWriteEnabled) rat.UpgradeOpen();
                var rec = new RegAppTableRecord { Name = name };
                rat.Add(rec);
                t.AddNewlyCreatedDBObject(rec, true);
            }
        }

        private Func<double, double, bool> PartPredicate(VolLabelKind kind,
            CivilSurface? s1, CivilSurface? s2)
        {
            if (s1 == null || s2 == null) return (lx, ly) => true;
            return kind switch
            {
                VolLabelKind.Fill => (lx, ly) => WorkAt(s1, s2, lx, ly) > 0,
                VolLabelKind.Cut  => (lx, ly) => WorkAt(s1, s2, lx, ly) < 0,
                _                 => (lx, ly) => LabelVisible(s1, s2, lx, ly),
            };
        }

        /// <summary>
        /// «Перерисовать»: после смены шрифта/высоты подписи объёмов заново
        /// ставятся без наездов на тройки отметок — так же, как при расчёте.
        /// Двигаются только подписи, которые стоят там, куда их поставила
        /// программа; перенесённые пользователем (вручную или выноской)
        /// остаются на месте и сами считаются препятствием.
        /// </summary>
        private void RelayoutVolumeLabels()
        {
            using var trans = _db.TransactionManager.StartTransaction();
            var rat = (RegAppTable)trans.GetObject(_db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(VolPosAppName)) { trans.Commit(); return; }

            var bt = (BlockTable)trans.GetObject(_db.BlockTableId, OpenMode.ForRead);
            _ms = (BlockTableRecord)trans.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var fixedTexts = new List<MText>();
            var movable    = new List<(MText Mt, double[] D, VolLabelKind Kind)>();
            foreach (ObjectId id in _ms)
            {
                if (trans.GetObject(id, OpenMode.ForRead) is not MText mt) continue;
                if (mt.Layer == _o.WorkLayerName || mt.Layer == _o.DesignLayerName ||
                    mt.Layer == _o.ExistLayerName)
                {
                    fixedTexts.Add(mt);
                    continue;
                }
                if (mt.Layer != _o.VolumeLayerName) continue;

                var rb = mt.GetXDataForApplication(VolPosAppName);
                double[]? d = null; short kind = 0;
                if (rb != null && mt.GetXDataForApplication(XAppName) == null)
                {
                    var reals = new List<double>();
                    foreach (TypedValue tv in rb)
                    {
                        if (tv.TypeCode == (int)DxfCode.ExtendedDataReal) reals.Add((double)tv.Value);
                        else if (tv.TypeCode == (int)DxfCode.ExtendedDataInteger16) kind = (short)tv.Value;
                    }
                    if (reals.Count == 11) d = reals.ToArray();
                }
                if (d == null) { fixedTexts.Add(mt); continue; }

                // Стоит ли там, куда её поставила программа (в её системе сетки)
                double cA0 = Math.Cos(d[2] * Math.PI / 180), sA0 = Math.Sin(d[2] * Math.PI / 180);
                double px = d[0] + d[9] * cA0 - d[10] * sA0;
                double py = d[1] + d[9] * sA0 + d[10] * cA0;
                double tol = Math.Max(mt.TextHeight * 0.01, 1e-6);
                if (Math.Abs(mt.Location.X - px) > tol || Math.Abs(mt.Location.Y - py) > tol)
                    fixedTexts.Add(mt);
                else
                    movable.Add((mt, d, (VolLabelKind)kind));
            }
            if (movable.Count == 0) { trans.Commit(); return; }

            // Поверхности и границы — чтобы подпись осталась в своей части
            // ячейки. Без них — только без наездов в пределах ячейки.
            CivilSurface? s1 = null, s2 = null;
            try
            {
                if (FindSurfaces(trans, CivilApplication.ActiveDocument, out s1, out s2))
                {
                    PrepareElevationCache(s1!, s2!);
                    LoadManualBoundaries(trans);
                }
                else s1 = s2 = null;
            }
            catch { s1 = s2 = null; }

            double baseX = _o.BaseX, baseY = _o.BaseY, rotDeg = _o.RotationDegrees;
            try
            {
                // Картограмм в чертеже может быть несколько — у каждой своя
                // система сетки; раскладываем по группам.
                foreach (var grp in movable.GroupBy(m => (m.D[0], m.D[1], m.D[2])))
                {
                    _o.BaseX = grp.Key.Item1; _o.BaseY = grp.Key.Item2; _o.RotationDegrees = grp.Key.Item3;
                    double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);

                    _labelBoxes.Clear();
                    foreach (var f in fixedTexts) _labelBoxes.Add(LocalBox(f));

                    foreach (var (mt, d, kind) in grp)
                    {
                        mt.UpgradeOpen();
                        mt.Location = LW(d[7], d[8], cA, sA);
                        PlaceVolumeLabel(mt, d[3], d[4], d[5], d[6], d[7], d[8],
                            PartPredicate(kind, s1, s2), kind);
                    }
                }
            }
            finally
            {
                _o.BaseX = baseX; _o.BaseY = baseY; _o.RotationDegrees = rotDeg;
            }
            trans.Commit();
        }

        // Годится ли точка под центр подписи объёма обычной ячейки — тот же
        // критерий «видимой части», что и в FindInsideAnchor.
        private bool LabelVisible(CivilSurface s1, CivilSurface s2, double lx, double ly)
        {
            bool manual = _boundaryPts != null || _innerPtsList != null;
            if (!manual && !_o.ClipCells) return true;
            double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);
            double wx = _o.BaseX + lx * cA - ly * sA;
            double wy = _o.BaseY + lx * sA + ly * cA;
            return manual
                ? IsInClipRegion(wx, wy)
                : GetElevS(s1, wx, wy).HasValue && GetElevS(s2, wx, wy).HasValue;
        }

        // Рабочая отметка в локальной точке; NaN — нет данных или вне границ.
        private double WorkAt(CivilSurface s1, CivilSurface s2, double lx, double ly)
        {
            double cA = Math.Cos(_o.RotationRadians), sA = Math.Sin(_o.RotationRadians);
            double wx = _o.BaseX + lx * cA - ly * sA;
            double wy = _o.BaseY + lx * sA + ly * cA;
            if (!IsInClipRegion(wx, wy) || InAnomalyZoneW(wx, wy)) return double.NaN;
            double? e1 = GetElevS(s1, wx, wy);
            double? e2 = GetElevS(s2, wx, wy);
            if (!e1.HasValue || !e2.HasValue) return double.NaN;
            return e2.Value - e1.Value;
        }

        private MText AddCenteredText(Transaction t, string layer, Point3d center, string text,
            double h, double angle, int aci, bool hideMask = false)
        {
            var mt = new MText
            {
                Location        = center,
                Contents        = text,
                TextHeight      = h,
                // Направление — мировым вектором: MText.Rotation
                // отсчитывается от оси X ТЕКУЩЕЙ ПСК, и при повёрнутой
                // ПСК текст уходил бы с угла сетки.
                Direction       = TextDir(angle),
                Layer           = layer,
                Color           = Color.FromColorIndex(ColorMethod.ByAci, (short)aci),
                TextStyleId     = _tsId,
                Attachment      = AttachmentPoint.MiddleCenter,
                Width           = 0
            };
            if (hideMask)
            {
                mt.BackgroundFill        = true;
                mt.BackgroundScaleFactor = 1.1;
            }
            _ms.AppendEntity(mt);
            t.AddNewlyCreatedDBObject(mt, true);
            return mt;
        }

        private string Signed(double v, int d = 2)
        {
            string s = Num(v, d);
            return v > 0 ? "+" + s : s;
        }

        /// <summary>
        /// Число для чертежа: d знаков после запятой и разделитель из настроек
        /// («.», «,» или без разделителя) — не зависит от региональных
        /// настроек Windows.
        /// </summary>
        private string Num(double v, int d = 2) =>
            v.ToString("F" + d, System.Globalization.CultureInfo.InvariantCulture)
             .Replace(".", _o.DecimalSeparator);

        // ═══════════════════════════════════════════════════════════════════════
        //  Обрезка ячеек по наружной границе (полилинии)
        // ═══════════════════════════════════════════════════════════════════════

        internal enum CellClass { Inside, Outside, Partial }

        /// <summary>Извлечь вершины полилинии в список Point2d (нормализовано CCW)</summary>
        // ═══════════════════════════════════════════════════════════════════════
        //  Универсальное извлечение замкнутого контура границы из объекта чертежа.
        //  Поддерживаются: 2D-полилиния (Polyline), 3D-полилиния (Polyline3d) и
        //  характерная линия Civil 3D (FeatureLine). Контур проецируется на план
        //  (Z отбрасывается), дуги идут хордами по вершинам — как и раньше для
        //  бульжей Polyline. Замкнутость: либо флаг Closed, либо конец совпадает
        //  с началом (часто 3D-полилинии и характерные линии рисуют с привязкой
        //  к первой точке, не замыкая флагом). Возвращает CCW-кольцо или null,
        //  если объект не поддерживается / не замкнут / вырожден.
        // ═══════════════════════════════════════════════════════════════════════
        /// <param name="ed">Если задан, диагностика отказа пишется в командную
        /// строку — иначе граница отбрасывается молча, и обрезка квадратов по
        /// ней просто перестаёт работать без единого объяснения почему.</param>
        /// <param name="role">Как называть границу в сообщении: «наружная»/«внутренняя».</param>
        internal static List<Point2d>? GetBoundaryPoints(Transaction trans, ObjectId id,
            Editor? ed = null, string role = "граница")
        {
            void Warn(string reason) =>
                ed?.WriteMessage($"\n[Картограмма] {role}: {reason} — контур не используется, обрезка по нему не применяется.");

            if (id.IsNull) return null;
            var obj = trans.GetObject(id, OpenMode.ForRead);

            // Опорные вершины (3D, лежат НА кривой) — по типу объекта
            var anchors = new List<Point3d>();
            Curve? crv = null;
            switch (obj)
            {
                case Polyline pl:
                    if (!IsClosedCurve(pl)) { Warn("полилиния не замкнута"); return null; }
                    crv = pl;
                    // ВАЖНО: GetPoint3dAt возвращает координаты в WCS,
                    // а GetPoint2dAt — в OCS полилинии. Ячейки сетки строятся в WCS,
                    // поэтому смешивать системы нельзя — иначе клиппинг даёт мусор.
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                        anchors.Add(pl.GetPoint3dAt(i));
                    break;

                case Polyline3d p3d:
                    if (!IsClosedCurve(p3d)) { Warn("3D-полилиния не замкнута"); return null; }
                    crv = p3d;
                    foreach (ObjectId vId in p3d)
                    {
                        var v = trans.GetObject(vId, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null) anchors.Add(v.Position);
                    }
                    break;

                case FeatureLine fl:
                    if (!IsClosedCurve(fl)) { Warn("характерная линия не замкнута"); return null; }
                    crv = fl;
                    // Все точки характерной линии (PI + точки отметок) по порядку
                    var col = fl.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints);
                    foreach (Point3d p in col)
                        anchors.Add(p);
                    break;

                default:
                    // Поддерживаются только Polyline/Polyline3d/FeatureLine — Circle,
                    // Region, Spline, Civil-Parcel и т.п. молча игнорировались.
                    Warn($"объект типа {obj.GetType().Name} не поддерживается " +
                         "(нужна замкнутая полилиния или характерная линия)");
                    return null;
            }
            // Дублирующее замыкание и повторы вершин убираем ДО аппроксимации:
            // пара из двух совпадающих точек (последняя = первая у контура,
            // замкнутого совпадением концов, или у GetPoints замкнутой
            // характерной линии) даёт вырожденную хорду длиной во весь контур —
            // рекурсия обвела бы его второй раз и полигон стал самоналегающим.
            for (int i = anchors.Count - 1; i > 0; i--)
                if (anchors[i].DistanceTo(anchors[i - 1]) < 1e-6) anchors.RemoveAt(i);
            while (anchors.Count > 1 &&
                   anchors[anchors.Count - 1].DistanceTo(anchors[0]) < 1e-6)
                anchors.RemoveAt(anchors.Count - 1);
            if (anchors.Count < 3) { Warn("меньше 3 вершин после очистки дублей"); return null; }

            // Дуговые сегменты (скругления характерных линий, бульжи полилиний)
            // аппроксимируем адаптивно: между опорными вершинами вставляем точки,
            // пока стрелка прогиба хорды не станет ≤ 1 см. Прямые участки при
            // этом не разбиваются (их середина лежит на хорде).
            var pts = FlattenClosedCurve(crv, anchors);

            // Убираем дублирующее замыкание (последняя точка = первая)
            // и вырожденные повторы соседних вершин
            for (int i = pts.Count - 1; i > 0; i--)
                if (pts[i].GetDistanceTo(pts[i - 1]) < 1e-9) pts.RemoveAt(i);
            if (pts.Count > 1 && pts[pts.Count - 1].GetDistanceTo(pts[0]) < 1e-9)
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) { Warn("меньше 3 вершин после разворачивания дуг"); return null; }

            NormalizeCcw(pts);
            return pts;
        }

        /// <summary>
        /// Плановая аппроксимация замкнутой кривой: опорные вершины + адаптивная
        /// вставка точек на криволинейных участках (рекурсивное деление пополам
        /// по длине дуги, критерий — стрелка прогиба хорды ≤ SagTol). Если API
        /// кривой отказывает (например, вершина спайн-полилинии не на кривой) —
        /// тихий фолбэк на прежнее поведение: хорды по опорным вершинам.
        /// </summary>
        private static List<Point2d> FlattenClosedCurve(Curve crv, List<Point3d> anchors)
        {
            const double SagTol   = 0.01;  // 1 см — максимальное отклонение хорды
            const int    MaxDepth = 10;    // ≤ 2^10 точек на сегмент (страховка)

            var plain = new List<Point2d>(anchors.Count);
            foreach (var a in anchors) plain.Add(new Point2d(a.X, a.Y));

            try
            {
                double totalLen = crv.GetDistanceAtParameter(crv.EndParam);
                if (totalLen <= 1e-9) return plain;

                var dists = new double[anchors.Count];
                for (int i = 0; i < anchors.Count; i++)
                    dists[i] = crv.GetDistAtPoint(anchors[i]);

                var outPts = new List<Point2d>();

                void Subdivide(double dA, Point2d pA, double dB, Point2d pB, int depth)
                {
                    if (depth >= MaxDepth || dB - dA <= 1e-6) return;
                    // Вырожденная хорда (pA≈pB): стрелку прогиба не измерить,
                    // деление ушло бы в полный обход контура — не делим.
                    if (pA.GetDistanceTo(pB) < 1e-6) return;
                    double dM = (dA + dB) / 2.0;
                    double dq = dM > totalLen ? dM - totalLen : dM;   // замыкающий сегмент
                    var m3 = crv.GetPointAtDist(dq);
                    var pM = new Point2d(m3.X, m3.Y);
                    if (DistPointToSegment(pM, pA, pB) <= SagTol) return;   // прямая
                    Subdivide(dA, pA, dM, pM, depth + 1);
                    outPts.Add(pM);
                    Subdivide(dM, pM, dB, pB, depth + 1);
                }

                for (int i = 0; i < anchors.Count; i++)
                {
                    int j = (i + 1) % anchors.Count;
                    double dA = dists[i], dB = dists[j];
                    if (j == 0 && dB <= dA + 1e-9) dB += totalLen;  // переход через шов
                    outPts.Add(plain[i]);
                    if (dB > dA + 1e-9)
                        Subdivide(dA, plain[i], dB, plain[j], 0);
                }
                return outPts;
            }
            catch
            {
                return plain;   // фолбэк: хорды по опорным вершинам (как раньше)
            }
        }

        /// <summary>Расстояние от точки до отрезка AB (в плане).</summary>
        internal static double DistPointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double len2 = abx * abx + aby * aby;
            if (len2 < 1e-18) return p.GetDistanceTo(a);
            double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = a.X + t * abx, qy = a.Y + t * aby;
            double dx = p.X - qx, dy = p.Y - qy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Замкнута ли кривая: флаг Closed либо конец совпал с началом.</summary>
        private static bool IsClosedCurve(Curve c)
        {
            if (c.Closed) return true;
            try { return c.StartPoint.DistanceTo(c.EndPoint) <= 1e-4; }
            catch { return false; }
        }

        /// <summary>
        /// Нормализует направление обхода кольца в CCW —
        /// алгоритм Сазерленда–Ходжмана работает только с CCW-полигонами.
        /// </summary>
        private static void NormalizeCcw(List<Point2d> pts)
        {
            double area = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % n];
                area += (b.X - a.X) * (b.Y + a.Y);
            }
            // Положительная "shoelace area по часовой" => CW => реверсим
            if (area > 0) pts.Reverse();
        }

        /// <summary>Точка внутри полигона — ray-casting</summary>
        internal static bool PointInPolygon(Point2d pt, List<Point2d> poly)
        {
            int n = poly.Count;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = poly[i].Y, yj = poly[j].Y;
                double xi = poly[i].X, xj = poly[j].X;
                if (((yi > pt.Y) != (yj > pt.Y)) &&
                    (pt.X < (xj - xi) * (pt.Y - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// «Обрезать» (ClipCells=true): клиппинг по наружной границе И по
        /// внутренним «дыркам» на уровне субузлов.
        /// «Не обрезать» (ClipCells=false): полигоны игнорируются; фильтрация
        /// целиком выполняется в BuildCells по классу ячейки, а внутри ячейки
        /// считается полный объём (как в авто-режиме).
        /// </summary>
        private bool IsInBounds(double wx, double wy)
        {
            if (!_o.ClipCells) return true;
            return IsInClipRegion(wx, wy);
        }

        /// <summary>
        /// Геометрическая принадлежность точки клип-области (внутри наружной
        /// границы и вне всех «дырок»). В отличие от IsInBounds, НЕ зависит от
        /// ClipCells — используется для отрисовки подписей и фильтрации
        /// ячеек вне зависимости от того, обрезаем ли мы геометрию.
        /// </summary>
        private bool IsInClipRegion(double wx, double wy)
        {
            if (_boundaryPts == null && _innerPtsList == null)
                return true;

            // Быстрый отсев: точка вне габаритов наружной границы — точно снаружи
            if (_boundaryPts != null &&
                (wx < _bndMinX || wx > _bndMaxX || wy < _bndMinY || wy > _bndMaxY))
                return false;

            var pt = new Point2d(wx, wy);

            if (_boundaryPts != null && !PointInPolygon(pt, _boundaryPts))
                return false;

            if (_innerPtsList != null)
            {
                foreach (var ipts in _innerPtsList)
                    if (PointInPolygon(pt, ipts))
                        return false;
            }

            return true;
        }

        /// <summary>Пересечение двух отрезков; возвращает true и параметр t на AB</summary>
        internal static bool SegmentIntersect(
            Point2d a, Point2d b, Point2d c, Point2d d,
            out double t, out Point2d hit)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double ex = d.X - c.X, ey = d.Y - c.Y;
            double denom = dx * ey - dy * ex;
            t = 0; hit = a;
            if (Math.Abs(denom) < 1e-12) return false;
            double s = ((c.X - a.X) * ey - (c.Y - a.Y) * ex) / denom;
            double u = ((c.X - a.X) * dy - (c.Y - a.Y) * dx) / denom;
            if (s < -1e-9 || s > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return false;
            t = s;
            hit = new Point2d(a.X + dx * s, a.Y + dy * s);
            return true;
        }

        /// <summary>Классифицировать ячейку относительно границы</summary>
        internal static CellClass ClassifyCell(Point2d[] corners, List<Point2d> boundary)
        {
            int inside = 0;
            foreach (var pt in corners)
                if (PointInPolygon(pt, boundary)) inside++;

            if (inside == 4) return CellClass.Inside;
            if (inside > 0)  return CellClass.Partial;

            // Все 4 угла снаружи, но граница может пересекать ячейку
            for (int i = 0; i < boundary.Count; i++)
            {
                int j = (i + 1) % boundary.Count;
                for (int k = 0; k < 4; k++)
                {
                    int l = (k + 1) % 4;
                    if (SegmentIntersect(boundary[i], boundary[j], corners[k], corners[l],
                            out _, out _))
                        return CellClass.Partial;
                }
            }

            // Граница может быть полностью внутри ячейки (нет пересечений рёбер,
            // все углы ячейки снаружи). Проверяем: если хотя бы одна вершина границы
            // внутри прямоугольника ячейки — ячейка Partial.
            if (boundary.Count > 0)
            {
                double minX = corners[0].X, maxX = corners[0].X;
                double minY = corners[0].Y, maxY = corners[0].Y;
                for (int i = 1; i < corners.Length; i++)
                {
                    if (corners[i].X < minX) minX = corners[i].X;
                    if (corners[i].X > maxX) maxX = corners[i].X;
                    if (corners[i].Y < minY) minY = corners[i].Y;
                    if (corners[i].Y > maxY) maxY = corners[i].Y;
                }
                foreach (var bp in boundary)
                {
                    if (bp.X > minX && bp.X < maxX && bp.Y > minY && bp.Y < maxY)
                        return CellClass.Partial;
                }
            }

            return CellClass.Outside;
        }

    }
}