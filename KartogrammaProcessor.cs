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

                EraseByLayer(trans, _ms, _o.GridLayerName);
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

                // Штриховка выемки/насыпи и линия нулевых работ — по тем же
                // ячейкам и по тому же признаку знака рабочей отметки, по
                // которому делятся объёмы. Ничего не считает, только рисует.
                DrawHatchAndZeroLine(trans, rows, cols, cosA, sinA, surf1!, surf2!);

                // === Подписи отметок (чёрная / красная / рабочая) ===
                // Отметки рисуются в каждом узле сетки (rows+1)×(cols+1).
                // Узлы вне клип-области пропускаются (метки на «оторванных»
                // узлах физического смысла не имеют).
                Report("Построение подписей отметок…", 9);
                var cells = BuildCells(surf1!, surf2!, rows, cols, cosA, sinA, area);
                for (int nr = 0; nr <= rows; nr++)
                for (int nc = 0; nc <= cols; nc++)
                    DrawNodeLabel(trans, nr, nc, rows, cols, cosA, sinA, surf1!, surf2!);

                // Дополнительные тройки в точках пересечения границ с линиями
                // сетки — это «углы» образованные обрезкой. Только в режиме
                // «Обрезать»: в «не обрезать» квадраты целые, новых углов нет.
                if (_o.ClipCells)
                    DrawBoundaryGridIntersectionLabels(trans, rows, cols, cosA, sinA, surf1!, surf2!);

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
                    int nLog = Math.Max(4, (int)Math.Ceiling(
                        Math.Max(_o.CellSizeX, _o.CellSizeY) / _o.VolumeNodeStep));
                    ed.WriteMessage(
                        $"\n[Картограмма] Расчёт объёмов: шаг {_o.VolumeNodeStep:F3} м → N={nLog} на ячейку...");
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
                            cosA, sinA, out fillRes[i], out cutRes[i]);
                        areaRes[i] = CalcCellEffectiveArea(dc.Row, dc.Col, surf1!, surf2!, cosA, sinA);
                    }
                    else
                    {
                        volRes[i] = CalcCellVolumeAccurate(dc.Row, dc.Col, surf1!, surf2!,
                            cosA, sinA, out areaRes[i], out fillRes[i], out cutRes[i]);
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

                // Отладка распределения: KARTOGRAMMA_DEBUG=1 → объём и площадь
                // каждой ячейки в командную строку (для сверки версий по ячейкам)
                if (Environment.GetEnvironmentVariable("KARTOGRAMMA_DEBUG") == "1")
                    for (int ci = 0; ci < totalCells; ci++)
                        ed.WriteMessage(
                            $"\n[Отладка] Ячейка строка {dataCells[ci].Row + 1}, колонка {dataCells[ci].Col + 1}: " +
                            $"V={volRes[ci]:F3} м³, S={areaRes[ci]:F2} м², " +
                            $"насыпь={fillRes[ci]:F3}, выемка={cutRes[ci]:F3}");

                // ── Фаза 2: подписи и итоги (последовательно — работа с чертежом) ──
                double totalArea = 0;
                for (int ci = 0; ci < totalCells; ci++)
                {
                    var dc = dataCells[ci];
                    int r = dc.Row, c = dc.Col;
                    double vol = volRes[ci], cellArea = areaRes[ci];

                    // Площадь перекрытия суммируем независимо от порога объёма
                    // (как в Civil — площадь не зависит от отсечения малых объёмов).
                    totalArea += cellArea;

                    double volFill = fillRes[ci], volCut = cutRes[ci];

                    // ── Ячейка с нулевой линией ───────────────────────────────
                    // Если через ячейку проходит линия нулевых работ, в ней
                    // есть и насыпь, и выемка. Сальдо их гасит и скрывает
                    // реальный объём работ, поэтому такие ячейки подписываются
                    // ДВУМЯ цифрами — каждая в своей части ячейки — и в итоги
                    // идут обеими частями. Мелкий «хвост» одного знака (меньше
                    // порога отображения) за разделение не считаем: это обычная
                    // однородная ячейка.
                    //
                    // Нижняя граница — не только заданный минимальный объём, но
                    // и половина последнего разряда: иначе при отключённом
                    // фильтре (MinVolume = 0) разделённой считалась бы КАЖДАЯ
                    // ячейка, и по всему чертежу пошли бы пары вида «+0.00».
                    double minShow = Math.Max(_o.MinVolume,
                        0.5 * Math.Pow(10, -_o.VolumePrecision));
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

                        AddCenteredText(trans, _o.VolumeLayerName,
                            LW(fLx, fLy, cosA, sinA),
                            Signed(volFill, _o.VolumePrecision),
                            vh, angle, aci, hideMask: _o.HideMaskVolume);
                        AddCenteredText(trans, _o.VolumeLayerName,
                            LW(cLx, cLy, cosA, sinA),
                            Signed(volCut, _o.VolumePrecision),
                            vh, angle, aci, hideMask: _o.HideMaskVolume);

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
                    AddCenteredText(trans, _o.VolumeLayerName,
                        LW(vLx, vLy, cosA, sinA),
                        Signed(vol, _o.VolumePrecision),
                        vh, angle, aci, hideMask: _o.HideMaskVolume);

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
                            out double zVol, out double zArea);
                        totalArea += zArea;
                        DrawZoneRect(trans, z, cosA, sinA);
                        ed.WriteMessage(
                            $"\n[Картограмма] Перепад #{zi + 1}: объём {Signed(zVol, _o.VolumePrecision)} м³, площадь {zArea:F2} м²");

                        if (Math.Abs(zVol) < _o.MinVolume) continue;

                        double zcx = (z.x0 + z.x1) / 2.0, zcy = (z.y0 + z.y1) / 2.0;
                        FindInsideAnchor(z.x0, z.y0, z.x1 - z.x0, z.y1 - z.y0,
                            zcx, zcy, cosA, sinA, surf1!, surf2!, out double zLx, out double zLy);
                        AddCenteredText(trans, _o.VolumeLayerName,
                            LW(zLx, zLy, cosA, sinA),
                            Signed(zVol, _o.VolumePrecision),
                            _o.VolumeTextHeight, angle, _o.ColorVolume,
                            hideMask: _o.HideMaskVolume);

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
                        minR, minC, totalArea);
                }

                Report("Фиксация транзакции…", 98);
                trans.Commit();

                ed.WriteMessage($"\n[Картограмма] Объём посчитан (шаг субсетки {_o.VolumeNodeStep:F3} м).");
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

                int n = EraseByLayer(trans, ms, _o.GridLayerName);
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
                      + EraseByLayer(trans, ms, _o.ZeroLineLayerName);
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
        //  рисуется линия-выноска, «+»-крестик под тройкой на новом месте и
        //  малый крестик в исходной точке. Если хоть одна метка не найдена —
        //  не переносится ничего (тройка ходит только целиком).
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

                var delta = target - origin;
                foreach (var mt in nearest)
                {
                    var w = (MText)trans.GetObject(mt!.ObjectId, OpenMode.ForWrite);
                    w.Location = w.Location + delta;
                }

                // Ручная тройка (DrawManualTriple) рисует под собой собственное
                // перекрестье — две линии на слое рабочих отметок с серединой
                // точно в узле. При переносе тройки старое перекрестье убираем,
                // иначе на чертеже остаются два креста: старый в исходной точке
                // и новый «+»-маркер на новом месте. Заодно снимаем «+»-маркер
                // предыдущей выноски (слой таблицы) — повторный перенос той же
                // тройки не копит кресты. Ищем по середине линии: у обеих линий
                // перекрестья она совпадает с узлом; линии сетки живут на своём
                // слое, а у линии-выноски середина в узле не лежит.
                double crossTol = sh * 0.5;
                var crossIds = new List<ObjectId>();
                foreach (ObjectId id in ms)
                {
                    if (trans.GetObject(id, OpenMode.ForRead) is not Line ln) continue;
                    if (ln.Layer != _o.WorkLayerName && ln.Layer != _o.TableLayerName) continue;
                    double mx = (ln.StartPoint.X + ln.EndPoint.X) * 0.5;
                    double my = (ln.StartPoint.Y + ln.EndPoint.Y) * 0.5;
                    double dx = mx - origin.X, dy = my - origin.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) <= crossTol)
                        crossIds.Add(id);
                }
                foreach (var id in crossIds)
                {
                    var ln = (Line)trans.GetObject(id, OpenMode.ForWrite);
                    ln.Erase();
                }

                // Линия-выноска из исходного узла к новому месту + «+»-маркер
                // в новом месте (разделяет тройку на 3 квадранта как у узла
                // сетки). В исходной точке никаких крестиков не рисуем: у тройки
                // должно остаться ровно одно перекрестье — на новом месте, а
                // исходную точку показывает начало линии-выноски.
                double arm = _o.SmallTextHeight * 1.2;
                double cA  = Math.Cos(_o.RotationRadians);
                double sA  = Math.Sin(_o.RotationRadians);

                var leader = new Line(origin, target)
                {
                    Layer = _o.TableLayerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, (short)_o.ColorTable)
                };
                var lineH = new Line(
                    new Point3d(target.X - arm * cA, target.Y - arm * sA, 0),
                    new Point3d(target.X + arm * cA, target.Y + arm * sA, 0))
                {
                    Layer = _o.TableLayerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, (short)_o.ColorTable)
                };
                var lineV = new Line(
                    new Point3d(target.X + arm * sA, target.Y - arm * cA, 0),
                    new Point3d(target.X - arm * sA, target.Y + arm * cA, 0))
                {
                    Layer = _o.TableLayerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, (short)_o.ColorTable)
                };
                ms.AppendEntity(leader);
                ms.AppendEntity(lineH);
                ms.AppendEntity(lineV);
                trans.AddNewlyCreatedDBObject(leader, true);
                trans.AddNewlyCreatedDBObject(lineH, true);
                trans.AddNewlyCreatedDBObject(lineV, true);

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
                AddRightAlignedText(trans, _o.WorkLayerName,
                    LW(nx - margin, ny + margin, cA, sA),
                    Signed(work, _o.TextPrecision), sh, ang, _o.ColorWork, _o.HideMaskText);

                // Проектная — слева-направо, выше и правее точки
                AddTextToLayer(trans, _o.DesignLayerName,
                    LW(nx + margin, ny + margin, cA, sA),
                    e2.Value.ToString(fmt), sh, ang, _o.ColorDesign, hideMask: _o.HideMaskText);

                // Существующая — слева-направо, ниже и правее точки
                AddTextToLayer(trans, _o.ExistLayerName,
                    LW(nx + margin, ny - margin - sh, cA, sA),
                    e1.Value.ToString(fmt), sh, ang, _o.ColorExisting, hideMask: _o.HideMaskText);

                // Крестик-маркер точки — как «+»-маркер выноски, по углу сетки.
                // Кладём на слой рабочих отметок (живёт и удаляется вместе с
                // тройкой), но цвет — стандартный белый/чёрный AutoCAD (ACI 7),
                // чтобы крестик читался как узел сетки, а не как рабочая отметка.
                double arm = sh * 1.2;
                var lineH = new Line(
                    new Point3d(pt.X - arm * cA, pt.Y - arm * sA, 0),
                    new Point3d(pt.X + arm * cA, pt.Y + arm * sA, 0))
                {
                    Layer = _o.WorkLayerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
                };
                var lineV = new Line(
                    new Point3d(pt.X + arm * sA, pt.Y - arm * cA, 0),
                    new Point3d(pt.X - arm * sA, pt.Y + arm * cA, 0))
                {
                    Layer = _o.WorkLayerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
                };
                _ms.AppendEntity(lineH);
                _ms.AppendEntity(lineV);
                trans.AddNewlyCreatedDBObject(lineH, true);
                trans.AddNewlyCreatedDBObject(lineV, true);

                trans.Commit();
                ed.WriteMessage(
                    $"\n[Картограмма] Тройка: чёрная {e1.Value.ToString(fmt)}, " +
                    $"красная {e2.Value.ToString(fmt)}, рабочая {Signed(work, _o.TextPrecision)}");
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
                        if (UpdateTextEntity(ent, _o.SmallTextHeight, aci, textTsId))
                            updated++;
                        continue;
                    }

                    // Слой объёмов: только текстовые подписи объёмов в ячейках
                    if (lay == _o.VolumeLayerName)
                    {
                        if (ent is DBText || ent is MText)
                        {
                            if (UpdateTextEntity(ent, _o.VolumeTextHeight, _o.ColorVolume, textTsId))
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
                            if (UpdateTextEntity(ent, _o.TableTextHeight, _o.ColorTable, tableTsId))
                                updated++;
                        }
                    }
                }

                trans.Commit();
                ed.WriteMessage($"\n[Картограмма] Обновлено {updated} объектов (внешний вид).");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА перерисовки: {ex.Message}");
            }
            return updated;
        }

        private static bool UpdateTextEntity(
            Autodesk.AutoCAD.DatabaseServices.Entity ent,
            double height, int aci, ObjectId textStyleId)
        {
            ent.UpgradeOpen();
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
        private double CalcCellVolumeAccurate(int r, int c,
            CivilSurface s1, CivilSurface s2, double cosA, double sinA, out double cellArea,
            out double volFill, out double volCut)
        {
            double szX = _o.CellSizeX;
            double szY = _o.CellSizeY;

            int    n  = Math.Max(4, (int)Math.Ceiling(Math.Max(szX, szY) / _o.VolumeNodeStep));
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

            for (int si = 0; si < n; si++)
            for (int sj = 0; sj < n; sj++)
            {
                // Нижний левый треугольник: BL, BR, TL
                vol += CalcSubTriVol(s1, s2,
                    wx[si,   sj  ], wy[si,   sj  ], h[si,   sj  ],
                    wx[si,   sj+1], wy[si,   sj+1], h[si,   sj+1],
                    wx[si+1, sj  ], wy[si+1, sj  ], h[si+1, sj  ],
                    out double aBL, out double fBL, out double cBL);
                cellArea += aBL;
                volFill  += fBL;
                volCut   += cBL;

                // Верхний правый треугольник: BR, TR, TL
                vol += CalcSubTriVol(s1, s2,
                    wx[si,   sj+1], wy[si,   sj+1], h[si,   sj+1],
                    wx[si+1, sj+1], wy[si+1, sj+1], h[si+1, sj+1],
                    wx[si+1, sj  ], wy[si+1, sj  ], h[si+1, sj  ],
                    out double aTR, out double fTR, out double cTR);
                cellArea += aTR;
                volFill  += fTR;
                volCut   += cTR;
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
            out double volFill, out double volCut)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            volFill = 0.0;
            volCut  = 0.0;

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
                    out volFill, out volCut, out _, out _);
                return volFill + volCut;
            }

            // Однородная клетка — сальдо не меняется, оно же и есть объём
            // своего знака.
            if (vol >= 0) volFill = vol; else volCut = vol;
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
            out double area, out double volFill, out double volCut)
        {
            area = 0.0;
            volFill = 0.0;
            volCut  = 0.0;
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
                    out volFill, out volCut, out _, out _);
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
                    out double f1, out double c1, out _, out _);
                ZeroLineSplit.Triangle(a2, hP, hV2, hQ,
                    out double f2, out double c2, out _, out _);
                volFill = f1 + f2;
                volCut  = c1 + c2;

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
                    out volFill, out volCut, out _, out _);
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

            var fill = CellClipper.Clip(szX, szY, ClipSampleSteps(), 12, HasData, out var loops);
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
        private void DrawHatchAndZeroLine(Transaction t, int rows, int cols,
            double cA, double sA, CivilSurface s1, CivilSurface s2)
        {
            bool wantFill = _o.HatchFill.Enabled;
            bool wantCut  = _o.HatchCut.Enabled;
            bool wantZero = _o.ZeroLine.Enabled;
            if (!wantFill && !wantCut && !wantZero) return;

            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            int    steps = ClipSampleSteps();
            var    ltId  = ResolveLinetype(t, _o.ZeroLine.LineType);
            bool   errorReported = false;
            int    hatches = 0, zeroChains = 0;

            for (int r = 0; r < rows; r++)
            {
                if ((r & 3) == 0)
                    Report($"Штриховка… {r}/{rows}", 5 + (int)(3.0 * r / Math.Max(rows, 1)));

                for (int c = 0; c < cols; c++)
                {
                    // Ровно те же ячейки, что и в сетке.
                    if (!IsCellDrawn(r, c, cA, sA)) continue;
                    if (!CellHasOverlap(r, c, s1, s2, cA, sA) &&
                        !CellHasOverlap(r, c, s1, s2, cA, sA, DenseOverlapSteps())) continue;

                    double x0 = c * szX, y0 = r * szY;

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

                    if (wantFill && nPos > 0 &&
                        DrawSignHatch(t, x0, y0, cA, sA, steps, H, true,
                            nNeg == 0 && nNan == 0, _o.HatchFill, ref errorReported))
                        hatches++;

                    if (wantCut && nNeg > 0 &&
                        DrawSignHatch(t, x0, y0, cA, sA, steps, H, false,
                            nPos == 0 && nNan == 0, _o.HatchCut, ref errorReported))
                        hatches++;

                    if (wantZero && nPos > 0 && nNeg > 0)
                    {
                        foreach (var chain in ZeroContour.Trace(szX, szY, steps, H))
                        {
                            if (chain.Count < 2) continue;
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
                    }
                }
            }

            _doc.Editor.WriteMessage(
                $"\n[Картограмма] Штриховка: {hatches} контуров, линия нулевых работ: {zeroChains} сегментов");
        }

        /// <summary>
        /// Заштриховать часть ячейки с рабочей отметкой заданного знака.
        /// Возвращает true, если штриховка создана.
        /// </summary>
        private bool DrawSignHatch(Transaction t, double x0, double y0,
            double cA, double sA, int steps, Func<double, double, double> h,
            bool positive, bool wholeCell, HatchSpec spec, ref bool errorReported)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
            var loops = new List<List<Point2d>>();

            if (wholeCell)
            {
                loops.Add(new List<Point2d>
                {
                    ToUcs2d(LW(x0,       y0,       cA, sA)),
                    ToUcs2d(LW(x0 + szX, y0,       cA, sA)),
                    ToUcs2d(LW(x0 + szX, y0 + szY, cA, sA)),
                    ToUcs2d(LW(x0,       y0 + szY, cA, sA)),
                });
            }
            else
            {
                bool Pred(double lx, double ly)
                {
                    double v = h(lx, ly);
                    if (double.IsNaN(v)) return false;
                    return positive ? v > 0 : v < 0;
                }

                var fill = CellClipper.Clip(szX, szY, steps, 12, Pred, out var raw);
                // Full здесь означает вырожденную сшивку (целую ячейку отсеяла
                // грубая разведка выше) — штриховать наугад не станем.
                if (fill != CellClipper.CellFill.Partial || raw.Count == 0) return false;

                foreach (var loop in raw)
                {
                    var pts = new List<Point2d>(loop.Count);
                    foreach (var p in loop)
                        pts.Add(ToUcs2d(LW(x0 + p.X, y0 + p.Y, cA, sA)));
                    loops.Add(pts);
                }
            }

            return AddHatch(t, loops, spec, ref errorReported);
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

                AddRightAlignedText(t, _o.WorkLayerName,
                    LW(lx - margin, ly + margin, cA, sA),
                    Signed(work, _o.TextPrecision), sh, ang, workAci, _o.HideMaskText);
                AddTextToLayer(t, _o.DesignLayerName,
                    LW(lx + margin, ly + margin, cA, sA),
                    e2.Value.ToString(fmt), sh, ang, _o.ColorDesign, hideMask: _o.HideMaskText);
                AddTextToLayer(t, _o.ExistLayerName,
                    LW(lx + margin, ly - margin - sh, cA, sA),
                    e1.Value.ToString(fmt), sh, ang, _o.ColorExisting, hideMask: _o.HideMaskText);
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
            AddRightAlignedText(t, _o.WorkLayerName,
                LW(nx - margin, ny + margin, cA, sA),
                Signed(work, _o.TextPrecision), sh, ang, workAci, _o.HideMaskText);

            // Проектная — слева-направо, выше и правее узла
            AddTextToLayer(t, _o.DesignLayerName,
                LW(nx + margin, ny + margin, cA, sA),
                e2.Value.ToString(fmt), sh, ang, _o.ColorDesign, hideMask: _o.HideMaskText);

            // Существующая — слева-направо, ниже и правее узла
            AddTextToLayer(t, _o.ExistLayerName,
                LW(nx + margin, ny - margin - sh, cA, sA),
                e1.Value.ToString(fmt), sh, ang, _o.ColorExisting, hideMask: _o.HideMaskText);
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
            double totalArea = 0)
        {
            var ed  = _doc.Editor;
            double szX = _o.CellSizeX, szY = _o.CellSizeY;
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

            string areaText  = $"Площадь: {totalArea:F2} м²  насыпь: +{totFill:F2} м³  выемка: –{totCut:F2} м³";

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
                string sFillTot = totFill > 0.001 ? "+" + totFill.ToString("F2") : "–";
                string sCutTot  = totCut  > 0.001 ? "–" + totCut.ToString("F2")  : "–";
                string longFill = sFillTot, longCut = sCutTot;
                for (int gr = 0; gr < rows; gr++)
                {
                    string fs = rowFill[gr] > 0.001 ? "+" + rowFill[gr].ToString("F2") : "–";
                    string cs = rowCut[gr]  > 0.001 ? "–" + rowCut[gr].ToString("F2")  : "–";
                    if (fs.Length > longFill.Length) longFill = fs;
                    if (cs.Length > longCut.Length)  longCut  = cs;
                }

                // ── Точная вёрстка по фактической ширине текста ──────────────
                // Ширину таблицы задают: самая длинная цифра в колонке (впритык)
                // и горизонтальные «Итого, м³»/«Всего, м³» на две колонки.
                double wItog  = MeasureTextWidth(t, "Итого, м³", sh);
                double wVsego = MeasureTextWidth(t, "Всего, м³", sh);
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
                    string fs = rowFill[gr] > 0.001 ? "+" + rowFill[gr].ToString("F2") : "–";
                    string cs = rowCut[gr]  > 0.001 ? "–" + rowCut[gr].ToString("F2")  : "–";
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
                SetCell(tbl, iItog,  0, "Итого, м³", sh, CellAlignment.MiddleCenter, tc);
                SetCell(tbl, iVsego, 0, "Всего, м³", sh, CellAlignment.MiddleCenter, tc);
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
                double txtY = offY - (vsegoH + totalH + sh * 1.5);
                AddTextToLayer(t, _o.TableLayerName,
                    LW(tblLocalX, txtY, cA, sA), areaText, sh, ang, tc, _tblTsId,
                    hideMask: _o.HideMaskTable);

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
                string sFillTot = totFill > 0.001 ? "+" + totFill.ToString("F2") : "–";
                string sCutTot  = totCut  > 0.001 ? "–" + totCut.ToString("F2")  : "–";

                // ── Точная вёрстка по фактической ширине текста ──────────────
                // Высоту таблицы задаёт ВЕРТИКАЛЬНОЕ «Итого, м³»: две строки
                // делят его длину пополам — текст помещается впритык.
                double wItog  = MeasureTextWidth(t, "Итого, м³", sh);
                double wVsego = MeasureTextWidth(t, "Всего, м³", sh);
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
                    string val = colFill[c] > 0.001 ? "+" + colFill[c].ToString("F2") : "–";
                    SetCell(tbl, 0, iData0 + c, val, sh, CellAlignment.MiddleCenter, tc);
                }
                SetCell(tbl, 0, iTotal, sFillTot, sh, CellAlignment.MiddleCenter, tc);

                // Выемка (строка 1)
                SetCell(tbl, 1, iLabel, "Выемка", sh, CellAlignment.MiddleCenter, tc);
                for (int c = 0; c < cols; c++)
                {
                    string val = colCut[c] > 0.001 ? "–" + colCut[c].ToString("F2") : "–";
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
                SetCell(tbl, 0, iItog,  "Итого, м³", sh, CellAlignment.MiddleCenter, tc);
                tbl.Cells[0, iItog].Contents[0].Rotation  = Math.PI * 0.5;
                SetCell(tbl, 0, iVsego, "Всего, м³", sh, CellAlignment.MiddleCenter, tc);
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
                    txtLocalY = offY - gap - tblH - sh * 1.5;
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
                AddTextToLayer(t, _o.TableLayerName,
                    LW(tblLocalX, txtLocalY, cA, sA), areaText, sh, ang, tc, _tblTsId,
                    hideMask: _o.HideMaskTable);

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
        private double MeasureTextWidth(Transaction t, string text, double height)
        {
            try
            {
                var txt = new DBText { TextString = text, Height = height };
                txt.SetDatabaseDefaults();
                if (!_tblTsId.IsNull) txt.TextStyleId = _tblTsId;
                _ms.AppendEntity(txt);
                t.AddNewlyCreatedDBObject(txt, true);
                var ext = txt.GeometricExtents;
                double w = ext.MaxPoint.X - ext.MinPoint.X;
                txt.Erase();
                return w;
            }
            catch { return text.Length * height * 0.62; }   // грубая оценка по числу знаков
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
            out double vol, out double area)
        {
            vol = 0; area = 0;
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
                vol  += (e2.Value - e1.Value) * dA;
                area += dA;
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

        private static int EraseByLayer(Transaction t, BlockTableRecord ms, string layerName)
        {
            var ids = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                try
                {
                    if (t.GetObject(id, OpenMode.ForRead) is AcadEntity ent &&
                        string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase))
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

        private void AddTextToLayer(Transaction t, string layer, Point3d pos, string text,
            double h, double angle, int aci, ObjectId styleId = default, bool hideMask = false)
        {
            var mt = new MText
            {
                Location        = pos,
                Contents        = text,
                TextHeight      = h,
                Rotation        = angle,
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
        }

        private void AddRightAlignedText(Transaction t, string layer, Point3d rightEdge, string text,
            double h, double angle, int aci, bool hideMask = false)
        {
            var mt = new MText
            {
                Location        = rightEdge,
                Contents        = text,
                TextHeight      = h,
                Rotation        = angle,
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
        }

        private void AddCenteredText(Transaction t, string layer, Point3d center, string text,
            double h, double angle, int aci, bool hideMask = false)
        {
            var mt = new MText
            {
                Location        = center,
                Contents        = text,
                TextHeight      = h,
                Rotation        = angle,
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
        }

        private static string Signed(double v, int d = 2)
        {
            string s = v.ToString("F" + d);
            return v > 0 ? "+" + s : s;
        }

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