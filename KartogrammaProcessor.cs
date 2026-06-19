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

                if (_o.AutoBasePoint) CalcAutoBasePoint(surf1!, surf2!);

                var tst = (TextStyleTable)trans.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
                _tsId    = tst.Has(_o.TextStyleName)      ? tst[_o.TextStyleName]      : _db.Textstyle;
                _tblTsId = tst.Has(_o.TableTextStyleName) ? tst[_o.TableTextStyleName] : _db.Textstyle;

                CalcAutoGrid(surf1!, surf2!, out int rows, out int cols);

                double angle = _o.RotationRadians;
                double cosA  = Math.Cos(angle), sinA = Math.Sin(angle);
                double area  = _o.CellSizeX * _o.CellSizeY;

                EnsureLayer(trans, _o.TextLayerName,   7);
                EnsureLayer(trans, _o.WorkLayerName,   _o.ColorWork);
                EnsureLayer(trans, _o.ExistLayerName,  _o.ColorExisting);
                EnsureLayer(trans, _o.DesignLayerName, _o.ColorDesign);
                EnsureLayer(trans, _o.VolumeLayerName, _o.ColorVolume);
                EnsureLayer(trans, _o.TableLayerName,  _o.ColorTable);

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

                // Загрузить наружную границу (если задана)
                _boundaryPts = null;
                _innerPtsList = null;
                if (!_o.AutoBounds && !_o.OuterBoundaryId.IsNull)
                {
                    var bndPl = trans.GetObject(_o.OuterBoundaryId, OpenMode.ForRead) as Polyline;
                    if (bndPl != null && bndPl.Closed)
                        _boundaryPts = GetPolylinePoints(bndPl);
                }
                if (!_o.AutoBounds && _o.InnerBoundaryIds != null && _o.InnerBoundaryIds.Count > 0)
                {
                    _innerPtsList = new List<List<Point2d>>();
                    foreach (var innerId in _o.InnerBoundaryIds)
                    {
                        if (innerId.IsNull) continue;
                        var ipl = trans.GetObject(innerId, OpenMode.ForRead) as Polyline;
                        if (ipl != null && ipl.Closed)
                            _innerPtsList.Add(GetPolylinePoints(ipl));
                    }
                }

                // === Перестроение сетки (только в зоне перекрытия поверхностей) ===
                Report("Построение сетки…", 2);
                DrawGridLines(trans, rows, cols, cosA, sinA, surf1!, surf2!);

                // === Подписи отметок (чёрная / красная / рабочая) ===
                // Отметки рисуются в каждом узле сетки (rows+1)×(cols+1).
                // Узлы вне клип-области пропускаются (метки на «оторванных»
                // узлах физического смысла не имеют).
                Report("Построение подписей отметок…", 5);
                var cells = BuildCells(surf1!, surf2!, rows, cols, cosA, sinA, area);
                for (int nr = 0; nr <= rows; nr++)
                for (int nc = 0; nc <= cols; nc++)
                    DrawNodeLabel(trans, nr, nc, rows, cols, cosA, sinA, surf1!, surf2!);

                // Дополнительные тройки в точках пересечения границ с линиями
                // сетки — это «углы» образованные обрезкой. Только в режиме
                // «Обрезать»: в «не обрезать» квадраты целые, новых углов нет.
                if (!_o.DontClipCells)
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

                double totalArea = 0;
                int cellsDone = 0;
                foreach (var dc in dataCells)
                {
                    int r = dc.Row, c = dc.Col;
                    // Объём: два метода по выбору пользователя.
                    //   Triangulation — субтреугольная разбивка, точно.
                    //   Squares       — классический ручной метод S×(h1+h2+h3+h4)/4
                    //                   по отметкам в 4 углах ячейки.
                    double vol = _o.VolumeMethod == VolumeMethod.Squares
                        ? CalcCellVolumeSquares (r, c, surf1!, surf2!, cosA, sinA)
                        : CalcCellVolumeAccurate(r, c, surf1!, surf2!, cosA, sinA);
                    cellsDone++;

                    if (cellsDone % 50 == 0 || cellsDone == totalCells)
                        Report($"Объём: {cellsDone}/{totalCells} ячеек…",
                            10 + (int)(80.0 * cellsDone / totalCells));

                    if (vol == 0.0) continue;          // ячейка полностью вне зоны

                    // Эффективная площадь ячейки (только для ячеек в зоне перекрытия)
                    totalArea += CalcCellEffectiveArea(r, c, surf1!, surf2!, cosA, sinA);

                    double absVol = Math.Abs(vol);
                    if (absVol < _o.MinVolume) continue;

                    // vol > 0 = насыпь (design > exist), vol < 0 = выемка
                    bool isCut = vol < 0;
                    int aci    = _o.ColorVolume;

                    double x0 = c * _o.CellSizeX;
                    double y0 = r * _o.CellSizeY;
                    double vh = _o.VolumeTextHeight;

                    // Объём — по центру ячейки; для обрезанных ячеек подбираем
                    // ближайшую точку внутри клип-области, чтобы метка не
                    // попала за пределы оставшейся после обрезки геометрии.
                    double cx = x0 + _o.CellSizeX * 0.5;
                    double cy = y0 + _o.CellSizeY * 0.5;
                    FindInsideAnchor(x0, y0, _o.CellSizeX, _o.CellSizeY,
                        cx, cy, cosA, sinA, out double vLx, out double vLy);
                    AddCenteredText(trans, _o.VolumeLayerName,
                        LW(vLx, vLy, cosA, sinA),
                        Signed(vol, _o.VolumePrecision),
                        vh, angle, aci, hideMask: _o.HideMaskVolume);

                    // Округляем до точности отображения ДО суммирования, чтобы
                    // строчные/столбцовые/общие итоги совпадали с тем, что нарисовано
                    // в каждой ячейке (иначе двойное округление даёт расхождение
                    // на ±1 единицу младшего разряда: 0.704+0.026=0.730→"-0.73",
                    // а сумма видимых "-0.70"+"-0.02" = "-0.72").
                    // Парсим строку обратно — гарантированно совпадает с тем, что
                    // выведет ToString("Fn") в подписи ячейки.
                    double dispVol = double.Parse(
                        absVol.ToString("F" + _o.VolumePrecision,
                            System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (isCut) { totCut  += dispVol; colCut[c]  += dispVol; rowCut[r]  += dispVol; }
                    else       { totFill += dispVol; colFill[c] += dispVol; rowFill[r] += dispVol; }
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
                      + EraseByLayer(trans, ms, _o.TableLayerName);
                trans.Commit();

                ed.WriteMessage($"\n[Картограмма] Удалено {n} объектов (отметки + объёмы).\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] ОШИБКА удаления: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  По клику на любую из трёх цифр тройки находит точный узел сетки —
        //  пересечение, из которого должна стартовать выноска и где должен
        //  стоять разделительный крестик.
        //  Алгоритм: ищем ближайший MText на одном из трёх слоёв, затем
        //  все три метки тройки рядом с ним; из позиции рабочей метки (idx=0)
        //  вычисляем узел по известному смещению (margin, -margin) в локальных
        //  координатах сетки. Не зависит от BaseX/BaseY/CellSize.
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

                // По найденному MText ищем тройку в радиусе 75% ячейки
                var center = nearestAny.Location;
                double searchR = Math.Max(_o.CellSizeX, _o.CellSizeY) * 0.75;
                var found    = new MText?[3];
                var foundDist = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                foreach (ObjectId id in ms)
                {
                    var mt = trans.GetObject(id, OpenMode.ForRead) as MText;
                    if (mt == null) continue;
                    int idx = Array.IndexOf(layers, mt.Layer);
                    if (idx < 0) continue;
                    double d = (mt.Location - center).Length;
                    if (d > searchR) continue;
                    if (d < foundDist[idx]) { foundDist[idx] = d; found[idx] = mt; }
                }
                trans.Commit();

                double cA     = Math.Cos(_o.RotationRadians);
                double sA     = Math.Sin(_o.RotationRadians);
                double margin = _o.SmallTextHeight * 0.15;
                double sh     = _o.SmallTextHeight;

                // Рабочая (idx=0): якорь в локальных (nx-margin, ny+margin) от узла
                // ⇒ узел = рабочая_мир + rotate(+margin, -margin)
                if (found[0] != null)
                {
                    var p = found[0]!.Location;
                    return new Point3d(
                        p.X + margin * cA + margin * sA,
                        p.Y + margin * sA - margin * cA,
                        clickedPt.Z);
                }
                // Проектная (idx=1): якорь в (nx+margin, ny+margin)
                // ⇒ узел = проектная_мир + rotate(-margin, -margin)
                if (found[1] != null)
                {
                    var p = found[1]!.Location;
                    return new Point3d(
                        p.X - margin * cA + margin * sA,
                        p.Y - margin * sA - margin * cA,
                        clickedPt.Z);
                }
                // Существующая (idx=2): якорь в (nx+margin, ny-margin-sh)
                // ⇒ узел = существующая_мир + rotate(-margin, +margin+sh)
                if (found[2] != null)
                {
                    var p = found[2]!.Location;
                    return new Point3d(
                        p.X - margin * cA - (margin + sh) * sA,
                        p.Y - margin * sA + (margin + sh) * cA,
                        clickedPt.Z);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Выноска тройки отметок. Находит ближайшие MText на слоях «рабочая /
        //  проектная / существующая» в радиусе половины ячейки от origin,
        //  переносит их на target, и рисует «+»-маркер в origin для указания,
        //  откуда вынесена тройка. Маркер ложится на слой рабочей отметки —
        //  при следующем построении/удалении объёмов исчезнет вместе с ней.
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
                var nearest = new MText?[3];
                var nearestDist = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                double searchR = Math.Max(_o.CellSizeX, _o.CellSizeY) * 0.5;

                foreach (ObjectId id in ms)
                {
                    var mt = trans.GetObject(id, OpenMode.ForRead) as MText;
                    if (mt == null) continue;
                    int idx = Array.IndexOf(layers, mt.Layer);
                    if (idx < 0) continue;
                    double d = (mt.Location - origin).Length;
                    if (d > searchR) continue;
                    if (d < nearestDist[idx])
                    {
                        nearestDist[idx] = d;
                        nearest[idx]     = mt;
                    }
                }

                if (nearest[0] == null || nearest[1] == null || nearest[2] == null)
                {
                    ed.WriteMessage("\n[Картограмма] В указанном месте не найдена полная тройка отметок.");
                    trans.Commit();
                    return false;
                }

                var delta = target - origin;
                foreach (var mt in nearest)
                {
                    var w = (MText)trans.GetObject(mt!.ObjectId, OpenMode.ForWrite);
                    w.Location = w.Location + delta;
                }

                // Линия-выноска из исходного узла к новому месту + «+»-маркер
                // в новом месте (разделяет тройку на 3 квадранта как у узла сетки).
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

            // Проекция мировых координат в ЛОКАЛЬНЫЕ (ось X = направление сетки)
            double ToLX(double wx, double wy) => wx * cosA + wy * sinA;
            double ToLY(double wx, double wy) => -wx * sinA + wy * cosA;

            var e1 = s1.GeometricExtents;
            var e2 = s2.GeometricExtents;

            double w1 = e1.MaxPoint.X - e1.MinPoint.X, h1 = e1.MaxPoint.Y - e1.MinPoint.Y;
            double w2 = e2.MaxPoint.X - e2.MinPoint.X, h2 = e2.MaxPoint.Y - e2.MinPoint.Y;
            double area1 = w1 * h1, area2 = w2 * h2;

            CivilSurface smaller = area1 <= area2 ? s1 : s2;
            CivilSurface larger  = area1 <= area2 ? s2 : s1;
            var eLarger          = area1 <= area2 ? e2 : e1;

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

            var eSmall = smaller.GeometricExtents;

            // ── Шаг 1: ВСЕ вершины TIN меньшей поверхности в локальных координатах
            if (smaller is TinSurface tinSmall && tinSmall.Vertices.Count > 0)
            {
                ed.WriteMessage($"\n[Картограмма] Анализирую {tinSmall.Vertices.Count} вершин TIN...");
                foreach (TinSurfaceVertex v in tinSmall.Vertices)
                    UpdateBounds(v.Location.X, v.Location.Y);

                // Расширяем до GeometricExtents если граница близко (< 2 ячейки)
                double savedMinLX = minLX, savedMaxLX = maxLX;
                double savedMinLY = minLY, savedMaxLY = maxLY;
                double tmpMinLX = double.MaxValue, tmpMinLY = double.MaxValue;
                double tmpMaxLX = double.MinValue, tmpMaxLY = double.MinValue;
                void TmpBounds(double wx, double wy) {
                    double lx = ToLX(wx, wy), ly = ToLY(wx, wy);
                    if (lx < tmpMinLX) tmpMinLX = lx; if (lx > tmpMaxLX) tmpMaxLX = lx;
                    if (ly < tmpMinLY) tmpMinLY = ly; if (ly > tmpMaxLY) tmpMaxLY = ly;
                }
                TmpBounds(eSmall.MinPoint.X, eSmall.MinPoint.Y);
                TmpBounds(eSmall.MaxPoint.X, eSmall.MinPoint.Y);
                TmpBounds(eSmall.MinPoint.X, eSmall.MaxPoint.Y);
                TmpBounds(eSmall.MaxPoint.X, eSmall.MaxPoint.Y);

                if (tmpMinLX >= savedMinLX - 2*sx) minLX = Math.Min(minLX, tmpMinLX);
                if (tmpMinLY >= savedMinLY - 2*sy) minLY = Math.Min(minLY, tmpMinLY);
                if (tmpMaxLX <= savedMaxLX + 2*sx) maxLX = Math.Max(maxLX, tmpMaxLX);
                if (tmpMaxLY <= savedMaxLY + 2*sy) maxLY = Math.Max(maxLY, tmpMaxLY);

                // Обрезаем по локальному bounding box большой поверхности
                double clipMinLX = double.MaxValue, clipMinLY = double.MaxValue;
                double clipMaxLX = double.MinValue, clipMaxLY = double.MinValue;
                void ClipBounds(double wx, double wy) {
                    double lx = ToLX(wx, wy), ly = ToLY(wx, wy);
                    if (lx < clipMinLX) clipMinLX = lx; if (lx > clipMaxLX) clipMaxLX = lx;
                    if (ly < clipMinLY) clipMinLY = ly; if (ly > clipMaxLY) clipMaxLY = ly;
                }
                ClipBounds(eLarger.MinPoint.X, eLarger.MinPoint.Y);
                ClipBounds(eLarger.MaxPoint.X, eLarger.MinPoint.Y);
                ClipBounds(eLarger.MinPoint.X, eLarger.MaxPoint.Y);
                ClipBounds(eLarger.MaxPoint.X, eLarger.MaxPoint.Y);

                minLX = Math.Max(minLX, clipMinLX);
                minLY = Math.Max(minLY, clipMinLY);
                maxLX = Math.Min(maxLX, clipMaxLX);
                maxLY = Math.Min(maxLY, clipMaxLY);
            }
            // ── Шаг 2: меньшая не TinSurface — вершины большей
            else if (larger is TinSurface tinLarge)
            {
                ed.WriteMessage($"\n[Картограмма] Анализирую {tinLarge.Vertices.Count} вершин второй TIN...");
                foreach (TinSurfaceVertex v in tinLarge.Vertices)
                    if (GetElev(smaller, v.Location.X, v.Location.Y).HasValue)
                        UpdateBounds(v.Location.X, v.Location.Y);
            }
            // ── Шаг 3: fallback — пересечение bounding boxes
            else
            {
                ed.WriteMessage($"\n[Картограмма] Fallback: пересечение bounding boxes...");
                double mnX = Math.Max(e1.MinPoint.X, e2.MinPoint.X);
                double mnY = Math.Max(e1.MinPoint.Y, e2.MinPoint.Y);
                double mxX = Math.Min(e1.MaxPoint.X, e2.MaxPoint.X);
                double mxY = Math.Min(e1.MaxPoint.Y, e2.MaxPoint.Y);
                UpdateBounds(mnX, mnY); UpdateBounds(mxX, mnY);
                UpdateBounds(mnX, mxY); UpdateBounds(mxX, mxY);
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

            // Добавляем полуячейку отступа с каждой стороны — равномерное перекрытие поверхностей
            minLX -= sx * 0.5;  maxLX += sx * 0.5;
            minLY -= sy * 0.5;  maxLY += sy * 0.5;

            double realW = maxLX - minLX;
            double realH = maxLY - minLY;
            ed.WriteMessage($"\n[Картограмма] Зона (локальная, с отступами): {realW:F3}×{realH:F3} м");

            cols = Clamp((int)Math.Ceiling(realW / sx), 1, 500);
            rows = Clamp((int)Math.Ceiling(realH / sy), 1, 500);

            if (_o.AutoBasePoint)
            {
                // Центрируем: излишек равномерно по обеим сторонам
                double excessX = cols * sx - realW;
                double excessY = rows * sy - realH;
                SetBaseFromLocal(minLX - excessX / 2.0, minLY - excessY / 2.0, cosA, sinA);
                ed.WriteMessage($"\n[Картограмма] Базовая точка (авто, угол {_o.RotationDegrees:F4}°): X={_o.BaseX:F3}, Y={_o.BaseY:F3}");
            }
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

                    double? e1v = GetElev(s1, wx, wy);
                    double? e2v = GetElev(s2, wx, wy);

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
                            double? fe1 = GetElev(s1, fwx, fwy);
                            double? fe2 = GetElev(s2, fwx, fwy);
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
            CivilSurface s1, CivilSurface s2, double cosA, double sinA)
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

                double? e1 = GetElev(s1, wx[si, sj], wy[si, sj]);
                double? e2 = GetElev(s2, wx[si, sj], wy[si, sj]);

                // Точки вне допустимой области помечаются как NaN — CalcSubTriVol
                // обрезает субтреугольники на краю через бинарный поиск
                // (FindBoundaryPoint). IsInBounds сам учитывает DontClipCells:
                // «обрезать» → клипать наружную границу и «дырки»;
                // «не обрезать» → клипать только «дырки», наружная игнорируется.
                if (e1.HasValue && e2.HasValue && IsInBounds(wx[si, sj], wy[si, sj]))
                    h[si, sj] = e2.Value - e1.Value;
                else
                    h[si, sj] = double.NaN;
            }

            double vol = 0.0;

            for (int si = 0; si < n; si++)
            for (int sj = 0; sj < n; sj++)
            {
                // Нижний левый треугольник: BL, BR, TL
                vol += CalcSubTriVol(s1, s2,
                    wx[si,   sj  ], wy[si,   sj  ], h[si,   sj  ],
                    wx[si,   sj+1], wy[si,   sj+1], h[si,   sj+1],
                    wx[si+1, sj  ], wy[si+1, sj  ], h[si+1, sj  ]);

                // Верхний правый треугольник: BR, TR, TL
                vol += CalcSubTriVol(s1, s2,
                    wx[si,   sj+1], wy[si,   sj+1], h[si,   sj+1],
                    wx[si+1, sj+1], wy[si+1, sj+1], h[si+1, sj+1],
                    wx[si+1, sj  ], wy[si+1, sj  ], h[si+1, sj  ]);
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
            CivilSurface s1, CivilSurface s2, double cosA, double sinA)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;

            // Рабочие отметки в 4 углах ячейки
            double sum = 0.0;
            int    cnt = 0;
            for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
            {
                double lx = (c + dx) * szX;
                double ly = (r + dy) * szY;
                double wx = _o.BaseX + lx * cosA - ly * sinA;
                double wy = _o.BaseY + lx * sinA + ly * cosA;
                double? e1 = GetElev(s1, wx, wy);
                double? e2 = GetElev(s2, wx, wy);
                if (!e1.HasValue || !e2.HasValue) continue;
                sum += e2.Value - e1.Value;
                cnt++;
            }
            if (cnt == 0) return 0.0;
            double hAvg = sum / cnt;

            // Эффективная площадь с учётом обрезки
            double effArea;
            if (_o.DontClipCells || (_boundaryPts == null && _innerPtsList == null))
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

            return effArea * hAvg;
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

                if (!GetElev(s1, wx, wy).HasValue) continue;
                if (!GetElev(s2, wx, wy).HasValue) continue;
                if (!IsInClipRegion(wx, wy)) continue;

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
            double xC, double yC, double hC)
        {
            bool aOk = !double.IsNaN(hA);
            bool bOk = !double.IsNaN(hB);
            bool cOk = !double.IsNaN(hC);
            int  valid = (aOk ? 1 : 0) + (bOk ? 1 : 0) + (cOk ? 1 : 0);

            if (valid == 0) return 0.0;

            double fullArea = TriArea2D(xA, yA, xB, yB, xC, yC);
            if (fullArea < 1e-14) return 0.0;

            if (valid == 3)
                return fullArea * (hA + hB + hC) / 3.0;

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
                double area = TriArea2D(xV, yV, pX, pY, qX, qY);
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
                double? e1  = GetElev(s1, midX, midY);
                double? e2  = GetElev(s2, midX, midY);
                // Точка считается валидной только если обе поверхности
                // имеют данные И точка внутри полигонных границ.
                if (e1.HasValue && e2.HasValue && IsInBounds(midX, midY))
                    { loX = midX; loY = midY; }
                else
                    { hiX = midX; hiY = midY; }
            }

            // Берём последнюю валидную точку как граничную
            double? h1 = GetElev(s1, loX, loY);
            double? h2 = GetElev(s2, loX, loY);
            double hBnd = (h1.HasValue && h2.HasValue && IsInBounds(loX, loY))
                ? h2.Value - h1.Value : 0.0;
            return (loX, loY, hBnd);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Проверка: есть ли перекрытие обеих поверхностей в данной ячейке.
        //  Проверяем сетку 3×3 точек (центр, углы, середины рёбер) —
        //  если хотя бы одна точка имеет данные на ОБЕИХ поверхностях, ячейка валидна.
        // ═══════════════════════════════════════════════════════════════════════
        private bool CellHasOverlap(int r, int c,
            CivilSurface s1, CivilSurface s2, double cosA, double sinA)
        {
            double szX = _o.CellSizeX, szY = _o.CellSizeY;

            // 3×3 = 9 точек: углы + середины рёбер + центр
            for (int i = 0; i <= 2; i++)
            for (int j = 0; j <= 2; j++)
            {
                double lx = c * szX + j * szX * 0.5;
                double ly = r * szY + i * szY * 0.5;
                double wx = _o.BaseX + lx * cosA - ly * sinA;
                double wy = _o.BaseY + lx * sinA + ly * cosA;

                if (GetElev(s1, wx, wy).HasValue && GetElev(s2, wx, wy).HasValue)
                    return true;
            }

            return false;
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

            // Загрузить внешнюю границу и внутренние «дырки». Все строятся как
            // свежие плоские полилинии в плоскости Z=0 c Normal=(0,0,1) —
            // обязательно для Region.BooleanOperation, иначе eNonCoplanarGeometry.
            Polyline? outerProto = null;
            List<Point2d>? outerPts = null;
            if (!_o.AutoBounds && !_o.OuterBoundaryId.IsNull)
            {
                var bndPl = t.GetObject(_o.OuterBoundaryId, OpenMode.ForRead) as Polyline;
                if (bndPl != null && bndPl.Closed)
                {
                    outerProto = BuildFlatPolyline(bndPl);
                    outerPts   = GetPolylinePoints(bndPl);
                }
            }

            var innerProtos = new List<Polyline>();
            var innerPtsList = new List<List<Point2d>>();
            if (!_o.AutoBounds && _o.InnerBoundaryIds != null)
            {
                foreach (var innerId in _o.InnerBoundaryIds)
                {
                    if (innerId.IsNull) continue;
                    var ipl = t.GetObject(innerId, OpenMode.ForRead) as Polyline;
                    if (ipl != null && ipl.Closed)
                    {
                        innerProtos.Add(BuildFlatPolyline(ipl));
                        innerPtsList.Add(GetPolylinePoints(ipl));
                    }
                }
            }

            bool hasManualBounds = (outerProto != null && outerPts != null) || innerProtos.Count > 0;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    double x0 = c * szX, y0 = r * szY;
                    var corners = new Point2d[4];
                    corners[0] = ToUcs2d(LW(x0,       y0,       cA, sA));
                    corners[1] = ToUcs2d(LW(x0 + szX, y0,       cA, sA));
                    corners[2] = ToUcs2d(LW(x0 + szX, y0 + szY, cA, sA));
                    corners[3] = ToUcs2d(LW(x0,       y0 + szY, cA, sA));

                    // Базовый фильтр: ячейка должна перекрываться обеими поверхностями
                    if (!CellHasOverlap(r, c, s1, s2, cA, sA))
                        continue;

                    if (hasManualBounds)
                    {
                        // Классификация по внешней границе — работает всегда.
                        CellClass outerCls = (outerPts != null)
                            ? ClassifyCell(corners, outerPts)
                            : CellClass.Inside;
                        if (outerCls == CellClass.Outside) continue;

                        // Классификация по внутренним границам: если ячейка целиком
                        // внутри хотя бы одной — пропускаем; иначе собираем пересекающиеся.
                        var partialInners = new List<Polyline>();
                        bool insideAnyInner = false;
                        for (int k = 0; k < innerPtsList.Count; k++)
                        {
                            var iCls = ClassifyCell(corners, innerPtsList[k]);
                            if (iCls == CellClass.Inside) { insideAnyInner = true; break; }
                            if (iCls == CellClass.Partial) partialInners.Add(innerProtos[k]);
                        }
                        if (insideAnyInner) continue;

                        // Флаг DontClipCells управляет только тем, резать ли крайние
                        // ячейки. Если флаг включён — Partial-ячейки рисуются целиком.
                        bool needRegion = !_o.DontClipCells &&
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
                            continue;
                        }
                        // Fall through: целиком внутри outer без пересечений,
                        // либо DontClipCells=true и ячейка только задета границей.
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
            return drawn;
        }

        /// <summary>Построить плоскую копию полилинии в WCS Z=0 (для Region).</summary>
        private static Polyline BuildFlatPolyline(Polyline src)
        {
            var p = new Polyline();
            for (int i = 0; i < src.NumberOfVertices; i++)
            {
                var p3 = src.GetPoint3dAt(i); // WCS
                p.AddVertexAt(i, new Point2d(p3.X, p3.Y), 0, 0, 0);
            }
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
                double? e1 = GetElev(s1, wx, wy);
                double? e2 = GetElev(s2, wx, wy);
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
        /// Подбирает якорь метки, гарантированно попадающий внутрь клип-области
        /// (наружная граница минус внутренние «дырки»). Если исходная точка уже
        /// внутри — возвращает её. Иначе сэмплирует 7×7 точек по ячейке и берёт
        /// ближайшую к исходной, прошедшую IsInBounds. Если подходящих нет —
        /// возвращает исходную (fallback). В режиме без границ — всегда исходная.
        /// </summary>
        private void FindInsideAnchor(
            double cellX0, double cellY0, double szX, double szY,
            double ancLx, double ancLy, double cA, double sA,
            out double outLx, out double outLy)
        {
            double awx = _o.BaseX + ancLx * cA - ancLy * sA;
            double awy = _o.BaseY + ancLx * sA + ancLy * cA;
            if (IsInBounds(awx, awy))
            {
                outLx = ancLx; outLy = ancLy;
                return;
            }

            const int N = 7;
            double best = double.MaxValue;
            double bx = ancLx, by = ancLy;
            bool found = false;
            for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                double lx = cellX0 + szX * (j + 0.5) / N;
                double ly = cellY0 + szY * (i + 0.5) / N;
                double wx = _o.BaseX + lx * cA - ly * sA;
                double wy = _o.BaseY + lx * sA + ly * cA;
                if (!IsInBounds(wx, wy)) continue;
                double dx = lx - ancLx, dy = ly - ancLy;
                double d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; bx = lx; by = ly; found = true; }
            }
            outLx = found ? bx : ancLx;
            outLy = found ? by : ancLy;
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
            if (_o.DontClipCells)
            {
                if (!IsAnyAdjacentCellDrawn(nr, nc, rows, cols, cA, sA)) return;
            }
            else
            {
                if (!IsInClipRegion(wx, wy)) return;
            }

            double? e1 = GetElev(s1, wx, wy);
            double? e2 = GetElev(s2, wx, wy);
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
            // Высота шрифта таблицы — не более 40% от минимального шага сетки,
            // чтобы текст гарантированно помещался в ячейку
            double sh  = Math.Min(_o.TableTextHeight, Math.Min(szX, szY) * 0.4);
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
                double gap    = szX;
                double colW   = szX;                                    // ширина каждого столбца = шаг X
                double itogH  = sh * 2.0;                              // высота строки «Итого, м³» — впритык к тексту
                double labelH = Math.Max(szY * 1.5,   sh * 7.0);       // высота строки заголовков «Насыпь/Выемка»
                double vsegoH = sh * 2.0;                              // высота строки «Всего, м³» — впритык к тексту
                double totalH = szY;                                    // высота строки итогов

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
                SetCell(tbl, iTotal, 0,
                    totFill > 0.001 ? "+" + totFill.ToString("F2") : "–",
                    sh, CellAlignment.MiddleCenter, tc);
                SetCell(tbl, iTotal, 1,
                    totCut > 0.001 ? "–" + totCut.ToString("F2") : "–",
                    sh, CellAlignment.MiddleCenter, tc);

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
                double gap    = szY;
                double rH     = szY;
                double tblH   = 2.0 * rH;
                double labelW = Math.Max(szX * 1.5, sh * 7.0);
                double itogW  = sh * 2.0;                              // ширина «Итого, м³» — впритык к тексту
                double vsegoW = sh * 2.0;                              // ширина «Всего, м³» — впритык к тексту
                double totalW = Math.Max(szX * 1.5, sh * 7.0);

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
                SetCell(tbl, 0, iTotal,
                    totFill > 0.001 ? "+" + totFill.ToString("F2") : "–",
                    sh, CellAlignment.MiddleCenter, tc);

                // Выемка (строка 1)
                SetCell(tbl, 1, iLabel, "Выемка", sh, CellAlignment.MiddleCenter, tc);
                for (int c = 0; c < cols; c++)
                {
                    string val = colCut[c] > 0.001 ? "–" + colCut[c].ToString("F2") : "–";
                    SetCell(tbl, 1, iData0 + c, val, sh, CellAlignment.MiddleCenter, tc);
                }
                SetCell(tbl, 1, iTotal,
                    totCut > 0.001 ? "–" + totCut.ToString("F2") : "–",
                    sh, CellAlignment.MiddleCenter, tc);

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
        private static List<Point2d> GetPolylinePoints(Polyline pl)
        {
            // ВАЖНО: GetPoint3dAt возвращает координаты в WCS,
            // а GetPoint2dAt — в OCS полилинии. Ячейки сетки строятся в WCS,
            // поэтому смешивать системы нельзя — иначе клиппинг даёт мусор.
            var pts = new List<Point2d>(pl.NumberOfVertices);
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                var p3 = pl.GetPoint3dAt(i);
                pts.Add(new Point2d(p3.X, p3.Y));
            }

            // Нормализуем направление обхода в CCW —
            // алгоритм Сазерленда–Ходжмана работает только с CCW-полигонами
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
            return pts;
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
        /// «Обрезать» (DontClipCells=false): клиппинг по наружной границе И по
        /// внутренним «дыркам» на уровне субузлов.
        /// «Не обрезать» (DontClipCells=true): полигоны игнорируются; фильтрация
        /// целиком выполняется в BuildCells по классу ячейки, а внутри ячейки
        /// считается полный объём (как в авто-режиме).
        /// </summary>
        private bool IsInBounds(double wx, double wy)
        {
            if (_o.DontClipCells) return true;
            return IsInClipRegion(wx, wy);
        }

        /// <summary>
        /// Геометрическая принадлежность точки клип-области (внутри наружной
        /// границы и вне всех «дырок»). В отличие от IsInBounds, НЕ зависит от
        /// DontClipCells — используется для отрисовки подписей и фильтрации
        /// ячеек вне зависимости от того, обрезаем ли мы геометрию.
        /// </summary>
        private bool IsInClipRegion(double wx, double wy)
        {
            if (_boundaryPts == null && _innerPtsList == null)
                return true;

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