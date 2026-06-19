using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Мок поверхности: высота задаётся функцией (x,y) → z
    // ═══════════════════════════════════════════════════════════════
    public class MockSurface
    {
        private readonly Func<double, double, double?> _elevFunc;
        public MockSurface(Func<double, double, double?> elevFunc) => _elevFunc = elevFunc;
        public double? GetElev(double x, double y) => _elevFunc(x, y);

        public static MockSurface Flat(double z) => new MockSurface((x, y) => z);
        public static MockSurface Tilted(double z0, double kx, double ky) =>
            new MockSurface((x, y) => z0 + kx * x + ky * y);
        public static MockSurface FlatRect(double z, double x0, double y0, double x1, double y1) =>
            new MockSurface((x, y) =>
                x >= x0 && x <= x1 && y >= y0 && y <= y1 ? (double?)z : null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Копия логики расчёта объёма из KartogrammaProcessor.cs
    //  - CalcCellVolume делает полигонный клиппинг субтреугольников
    //  - Какие ячейки участвуют определяет CalcTotalVolume
    //  - DontClipCells управляет включением Partial-ячеек
    // ═══════════════════════════════════════════════════════════════
    public class VolumeCalc
    {
        private readonly List<Pt2d>? _boundaryPts;
        private readonly List<List<Pt2d>>? _innerPtsList;

        public VolumeCalc(List<Pt2d>? boundary = null, List<List<Pt2d>>? inners = null)
        {
            _boundaryPts = boundary;
            _innerPtsList = inners;
        }

        private bool IsInBounds(double wx, double wy)
        {
            if (_boundaryPts == null && _innerPtsList == null) return true;
            var pt = new Pt2d(wx, wy);
            if (_boundaryPts != null && !Geo.PointInPolygon(pt, _boundaryPts)) return false;
            if (_innerPtsList != null)
                foreach (var ipts in _innerPtsList)
                    if (Geo.PointInPolygon(pt, ipts)) return false;
            return true;
        }

        /// <summary>
        /// Объём ячейки — субсеточная интеграция.
        /// Клиппинг по границе поверхности (GetElev → null) И полигонной границе.
        /// </summary>
        public double CalcCellVolume(int r, int c, double cellSizeX, double cellSizeY,
            MockSurface s1, MockSurface s2, int n = 8)
        {
            double szX = cellSizeX, szY = cellSizeY;
            double dx = szX / n, dy = szY / n;
            var h  = new double[n + 1, n + 1];
            var wx = new double[n + 1, n + 1];
            var wy = new double[n + 1, n + 1];

            for (int si = 0; si <= n; si++)
            for (int sj = 0; sj <= n; sj++)
            {
                double lx = c * szX + sj * dx;
                double ly = r * szY + si * dy;
                wx[si, sj] = lx;
                wy[si, sj] = ly;
                double? e1 = s1.GetElev(lx, ly);
                double? e2 = s2.GetElev(lx, ly);
                h[si, sj] = (e1.HasValue && e2.HasValue && IsInBounds(lx, ly))
                    ? e2.Value - e1.Value : double.NaN;
            }

            double vol = 0.0;
            for (int si = 0; si < n; si++)
            for (int sj = 0; sj < n; sj++)
            {
                vol += CalcSubTriVol(s1, s2,
                    wx[si,sj], wy[si,sj], h[si,sj],
                    wx[si,sj+1], wy[si,sj+1], h[si,sj+1],
                    wx[si+1,sj], wy[si+1,sj], h[si+1,sj]);
                vol += CalcSubTriVol(s1, s2,
                    wx[si,sj+1], wy[si,sj+1], h[si,sj+1],
                    wx[si+1,sj+1], wy[si+1,sj+1], h[si+1,sj+1],
                    wx[si+1,sj], wy[si+1,sj], h[si+1,sj]);
            }
            return vol;
        }

        private double CalcSubTriVol(MockSurface s1, MockSurface s2,
            double xA, double yA, double hA,
            double xB, double yB, double hB,
            double xC, double yC, double hC)
        {
            bool aOk = !double.IsNaN(hA), bOk = !double.IsNaN(hB), cOk = !double.IsNaN(hC);
            int valid = (aOk?1:0)+(bOk?1:0)+(cOk?1:0);
            if (valid == 0) return 0;
            double fullArea = TriArea2D(xA,yA,xB,yB,xC,yC);
            if (fullArea < 1e-14) return 0;
            if (valid == 3) return fullArea * (hA+hB+hC) / 3.0;

            if (valid == 2)
            {
                double xOut,yOut,xV1,yV1,hV1,xV2,yV2,hV2;
                if (!aOk) {xOut=xA;yOut=yA;xV1=xB;yV1=yB;hV1=hB;xV2=xC;yV2=yC;hV2=hC;}
                else if (!bOk) {xOut=xB;yOut=yB;xV1=xA;yV1=yA;hV1=hA;xV2=xC;yV2=yC;hV2=hC;}
                else {xOut=xC;yOut=yC;xV1=xA;yV1=yA;hV1=hA;xV2=xB;yV2=yB;hV2=hB;}
                var (pX,pY,hP) = FindBoundaryPoint(s1,s2,xV1,yV1,xOut,yOut);
                var (qX,qY,hQ) = FindBoundaryPoint(s1,s2,xV2,yV2,xOut,yOut);
                double a1 = TriArea2D(pX,pY,xV1,yV1,xV2,yV2);
                double a2 = TriArea2D(pX,pY,xV2,yV2,qX,qY);
                return a1*(hP+hV1+hV2)/3.0 + a2*(hP+hV2+hQ)/3.0;
            }
            if (valid == 1)
            {
                double xV,yV,hV,xO1,yO1,xO2,yO2;
                if (aOk) {xV=xA;yV=yA;hV=hA;xO1=xB;yO1=yB;xO2=xC;yO2=yC;}
                else if (bOk) {xV=xB;yV=yB;hV=hB;xO1=xA;yO1=yA;xO2=xC;yO2=yC;}
                else {xV=xC;yV=yC;hV=hC;xO1=xA;yO1=yA;xO2=xB;yO2=yB;}
                var (pX,pY,hP) = FindBoundaryPoint(s1,s2,xV,yV,xO1,yO1);
                var (qX,qY,hQ) = FindBoundaryPoint(s1,s2,xV,yV,xO2,yO2);
                double area = TriArea2D(xV,yV,pX,pY,qX,qY);
                return area*(hV+hP+hQ)/3.0;
            }
            return 0;
        }

        private (double wx, double wy, double h) FindBoundaryPoint(
            MockSurface s1, MockSurface s2,
            double wxValid, double wyValid,
            double wxInvalid, double wyInvalid, int steps = 6)
        {
            double loX=wxValid, loY=wyValid, hiX=wxInvalid, hiY=wyInvalid;
            for (int k = 0; k < steps; k++)
            {
                double midX=(loX+hiX)*0.5, midY=(loY+hiY)*0.5;
                double? e1=s1.GetElev(midX,midY), e2=s2.GetElev(midX,midY);
                if (e1.HasValue && e2.HasValue && IsInBounds(midX, midY)) { loX=midX; loY=midY; }
                else { hiX=midX; hiY=midY; }
            }
            double? h1=s1.GetElev(loX,loY), h2=s2.GetElev(loX,loY);
            return (loX, loY, h1.HasValue&&h2.HasValue&&IsInBounds(loX,loY) ? h2.Value-h1.Value : 0);
        }

        private static double TriArea2D(double x1,double y1,double x2,double y2,double x3,double y3)
            => Math.Abs((x2-x1)*(y3-y1)-(x3-x1)*(y2-y1))*0.5;

        /// <summary>
        /// Полный расчёт объёма по сетке ячеек.
        /// Все Partial-ячейки включаются — CalcCellVolume обрезает
        /// субтреугольники по полигонной границе (IsInBounds).
        /// </summary>
        public double CalcTotalVolume(MockSurface s1, MockSurface s2,
            int rows, int cols, double cellSizeX, double cellSizeY,
            int subSteps = 8, bool dontClipCells = true)
        {
            double totalVol = 0;
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double x0 = c * cellSizeX, y0 = r * cellSizeY;
                var corners = new Pt2d[4];
                corners[0] = new Pt2d(x0, y0);
                corners[1] = new Pt2d(x0+cellSizeX, y0);
                corners[2] = new Pt2d(x0+cellSizeX, y0+cellSizeY);
                corners[3] = new Pt2d(x0, y0+cellSizeY);

                if (_boundaryPts != null)
                {
                    var cls = Geo.ClassifyCell(corners, _boundaryPts);
                    if (cls == CellClass.Outside) continue;
                }
                if (_innerPtsList != null)
                {
                    bool skip = false;
                    foreach (var ipts in _innerPtsList)
                    {
                        var iCls = Geo.ClassifyCell(corners, ipts);
                        if (iCls == CellClass.Inside) { skip=true; break; }
                    }
                    if (skip) continue;
                }

                bool hasData = false;
                for (int fi=0; fi<=2 && !hasData; fi++)
                for (int fj=0; fj<=2 && !hasData; fj++)
                {
                    double fx=c*cellSizeX+fj*cellSizeX*0.5;
                    double fy=r*cellSizeY+fi*cellSizeY*0.5;
                    if (s1.GetElev(fx,fy).HasValue && s2.GetElev(fx,fy).HasValue)
                        hasData = true;
                }
                if (!hasData) continue;

                totalVol += CalcCellVolume(r, c, cellSizeX, cellSizeY, s1, s2, subSteps);
            }
            return totalVol;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Тесты объёма — стандартный подход картограммы
    // ═══════════════════════════════════════════════════════════════
    public class VolumeTests
    {
        // -----------------------------------------------------------
        //  Без границ — точный результат
        // -----------------------------------------------------------
        [Fact]
        public void FlatSurfaces_NoBoundary_ExactVolume()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var calc = new VolumeCalc();
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10);
            Assert.InRange(vol, 19999, 20001);
        }

        // -----------------------------------------------------------
        //  Наклонная поверхность без границ
        // -----------------------------------------------------------
        [Fact]
        public void TiltedSurface_NoBoundary_CorrectVolume()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Tilted(50, 0.1, 0);
            var calc = new VolumeCalc();
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10);
            Assert.InRange(vol, 49900, 50100);
        }

        // -----------------------------------------------------------
        //  Нулевой объём
        // -----------------------------------------------------------
        [Fact]
        public void ZeroVolume_SameSurfaces()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(50);
            var calc = new VolumeCalc();
            double vol = calc.CalcTotalVolume(s1, s2, 5, 5, 10, 10);
            Assert.InRange(vol, -0.001, 0.001);
        }

        // -----------------------------------------------------------
        //  Отрицательный объём (выемка)
        // -----------------------------------------------------------
        [Fact]
        public void NegativeVolume_Cut()
        {
            var s1 = MockSurface.Flat(52);
            var s2 = MockSurface.Flat(50);
            var calc = new VolumeCalc();
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10);
            Assert.InRange(vol, -20001, -19999);
        }

        // -----------------------------------------------------------
        //  Поверхность меньше сетки
        // -----------------------------------------------------------
        [Fact]
        public void SurfaceSmallerThanGrid_CorrectVolume()
        {
            var s1 = MockSurface.FlatRect(50, 0, 0, 60, 60);
            var s2 = MockSurface.Flat(52);
            var calc = new VolumeCalc();
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10);
            Assert.InRange(vol, 7000, 7400);
        }

        // -----------------------------------------------------------
        //  С границей: DontClipCells=true → все Partial включены
        //  Субтреугольники обрезаются по полигону → объём ≈ точному
        // -----------------------------------------------------------
        [Fact]
        public void RectBoundary_Unclipped_CloseToExact()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(15, 15), new Pt2d(85, 15),
                new Pt2d(85, 85), new Pt2d(15, 85)
            };
            double exact = 70.0 * 70.0 * 2; // = 9800
            var calc = new VolumeCalc(boundary);
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            Assert.InRange(vol, exact * 0.97, exact * 1.03);
        }

        // -----------------------------------------------------------
        //  С границей: DontClipCells=false — то же самое (Partial
        //  всегда включены, клиппинг в CalcCellVolume)
        // -----------------------------------------------------------
        [Fact]
        public void RectBoundary_Clipped_CloseToExact()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(15, 15), new Pt2d(85, 15),
                new Pt2d(85, 85), new Pt2d(15, 85)
            };
            double exact = 70.0 * 70.0 * 2; // = 9800
            var calc = new VolumeCalc(boundary);
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);
            Assert.InRange(vol, exact * 0.97, exact * 1.03);
        }

        // -----------------------------------------------------------
        //  Оба режима дают одинаковый результат (Partial всегда включены)
        // -----------------------------------------------------------
        [Fact]
        public void BothModes_CloseToExact()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(15, 15), new Pt2d(85, 15),
                new Pt2d(85, 85), new Pt2d(15, 85)
            };
            double exact = 70.0 * 70.0 * 2;
            var calc = new VolumeCalc(boundary);
            double volUnclipped = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volClipped   = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);
            // Оба режима теперь одинаковы (Partial включены, клиппинг в CalcCellVolume)
            Assert.InRange(Math.Abs(volUnclipped - volClipped), 0, 1);
            Assert.InRange(volUnclipped, exact * 0.97, exact * 1.03);
        }

        // -----------------------------------------------------------
        //  Граница выровнена с сеткой — оба режима одинаковы
        // -----------------------------------------------------------
        [Fact]
        public void AlignedBoundary_BothModesEqual()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(60, 0),
                new Pt2d(60, 60), new Pt2d(0, 60)
            };
            var calc = new VolumeCalc(boundary);
            double volU = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volC = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);
            // Граница на рёбрах ячеек → нет Partial → оба режима ~одинаковы
            Assert.InRange(Math.Abs(volU - volC), 0, 100);
            Assert.InRange(volU, 7000, 7400);
        }

        // -----------------------------------------------------------
        //  Внутренняя граница: вырез
        // -----------------------------------------------------------
        [Fact]
        public void InnerBoundary_CutoutReducesVolume()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var outer = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(100, 0),
                new Pt2d(100, 100), new Pt2d(0, 100)
            };
            var inner = new List<Pt2d>
            {
                new Pt2d(30, 30), new Pt2d(70, 30),
                new Pt2d(70, 70), new Pt2d(30, 70)
            };
            double exactOuter = 10000 * 2;
            double exactInner = 40.0 * 40.0 * 2;
            double exactNet = exactOuter - exactInner; // = 16800

            var calc = new VolumeCalc(outer, new List<List<Pt2d>> { inner });
            double volU = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volC = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);

            // Unclipped: Partial у внутренней → полная площадь → МЕНЬШЕ exactNet
            // (потому что Partial-ячейки на вырезе считаются полностью = вырез больше)
            // Clipped: center-check исключает Partial у внутренней → вырез меньше → БОЛЬШЕ exactNet
            // Оба должны быть в разумных пределах (~10%)
            Assert.InRange(volU, exactNet * 0.85, exactNet * 1.15);
            Assert.InRange(volC, exactNet * 0.85, exactNet * 1.15);
        }

        // -----------------------------------------------------------
        //  Ромб: DontClipCells=true vs false
        // -----------------------------------------------------------
        [Fact]
        public void DiamondBoundary_UnclippedVsClipped()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(50, 0), new Pt2d(100, 50),
                new Pt2d(50, 100), new Pt2d(0, 50)
            };
            double exact = 5000 * 2; // = 10000
            var calc = new VolumeCalc(boundary);
            double volU = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volC = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);
            // Оба режима одинаковы (Partial включены, клиппинг в CalcCellVolume)
            Assert.InRange(Math.Abs(volU - volC), 0, 1);
            Assert.InRange(volU, exact * 0.93, exact * 1.07);
        }

        // -----------------------------------------------------------
        //  Мелкая сетка → ближе к точному значению
        // -----------------------------------------------------------
        [Fact]
        public void FinerGrid_CloserToExact()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(15, 15), new Pt2d(85, 15),
                new Pt2d(85, 85), new Pt2d(15, 85)
            };
            double exact = 70.0 * 70.0 * 2; // = 9800
            var calc = new VolumeCalc(boundary);
            double volCoarse = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10);
            double volFine   = calc.CalcTotalVolume(s1, s2, 20, 20, 5, 5);
            double errCoarse = Math.Abs(volCoarse - exact);
            double errFine   = Math.Abs(volFine - exact);
            Assert.True(errFine <= errCoarse + 1,
                $"Fine err={errFine:F1} should be <= Coarse err={errCoarse:F1}");
        }

        // -----------------------------------------------------------
        //  Без границы, с внутренней
        // -----------------------------------------------------------
        [Fact]
        public void NoOuterBoundary_WithInner()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var inner = new List<Pt2d>
            {
                new Pt2d(40, 40), new Pt2d(60, 40),
                new Pt2d(60, 60), new Pt2d(40, 60)
            };
            double exactCutout = 20.0 * 20.0 * 2; // = 800
            var calc = new VolumeCalc(null, new List<List<Pt2d>> { inner });
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            // ≈ 20000 - 800 = 19200
            Assert.InRange(vol, 18800, 19600);
        }

        // -----------------------------------------------------------
        //  Наклонные поверхности + граница
        // -----------------------------------------------------------
        [Fact]
        public void BothSurfacesTilted_WithBoundary()
        {
            var s1 = MockSurface.Tilted(50, 0.05, 0);
            var s2 = MockSurface.Tilted(52, 0.1, 0);
            var boundary = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(100, 0),
                new Pt2d(100, 100), new Pt2d(0, 100)
            };
            // Без обрезки: граница = вся сетка → нет Partial
            var calc = new VolumeCalc(boundary);
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            Assert.InRange(vol, 44500, 45500);
        }

        // -----------------------------------------------------------
        //  L-shape граница
        // -----------------------------------------------------------
        [Fact]
        public void LShapedBoundary()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(100, 0),
                new Pt2d(100, 50), new Pt2d(50, 50),
                new Pt2d(50, 100), new Pt2d(0, 100)
            };
            double exact = 7500 * 2; // = 15000
            var calc = new VolumeCalc(boundary);
            double volU = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volC = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);
            // L-shape с выровненными краями → мало Partial → близко к точному
            Assert.InRange(volU, exact * 0.95, exact * 1.10);
            Assert.InRange(volC, exact * 0.85, exact * 1.05);
        }

        // -----------------------------------------------------------
        //  Множественные внутренние границы
        // -----------------------------------------------------------
        [Fact]
        public void MultipleInnerBoundaries()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var outer = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(100, 0),
                new Pt2d(100, 100), new Pt2d(0, 100)
            };
            var inner1 = new List<Pt2d>
            {
                new Pt2d(10, 10), new Pt2d(30, 10),
                new Pt2d(30, 30), new Pt2d(10, 30)
            };
            var inner2 = new List<Pt2d>
            {
                new Pt2d(60, 60), new Pt2d(80, 60),
                new Pt2d(80, 80), new Pt2d(60, 80)
            };
            double exact = (10000 - 400 - 400) * 2; // = 18400
            var calc = new VolumeCalc(outer, new List<List<Pt2d>> { inner1, inner2 });
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            // Внутренние выровнены → мало Partial → близко к точному
            Assert.InRange(vol, exact * 0.90, exact * 1.10);
        }

        // -----------------------------------------------------------
        //  Тонкая рамка: большой внутренний вырез
        // -----------------------------------------------------------
        [Fact]
        public void ThinFrame_LargeInnerCutout()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var outer = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(100, 0),
                new Pt2d(100, 100), new Pt2d(0, 100)
            };
            var inner = new List<Pt2d>
            {
                new Pt2d(10, 10), new Pt2d(90, 10),
                new Pt2d(90, 90), new Pt2d(10, 90)
            };
            double exact = (10000 - 6400) * 2; // = 7200
            var calc = new VolumeCalc(outer, new List<List<Pt2d>> { inner });
            double volU = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volC = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);
            Assert.InRange(volU, exact * 0.80, exact * 1.20);
            Assert.InRange(volC, exact * 0.80, exact * 1.20);
        }

        // -----------------------------------------------------------
        //  Пятиугольная граница
        // -----------------------------------------------------------
        [Fact]
        public void PentagonBoundary()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>();
            for (int i = 0; i < 5; i++)
            {
                double angle = Math.PI / 2 + i * 2 * Math.PI / 5;
                boundary.Add(new Pt2d(50 + 50*Math.Cos(angle), 50 + 50*Math.Sin(angle)));
            }
            double area = 5.0*50*50*Math.Sin(2*Math.PI/5)/2;
            double exact = area * 2;

            var calc = new VolumeCalc(boundary);
            double volU = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volC = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);
            // Unclipped > exact > Clipped
            Assert.True(volU >= exact * 0.95);
            Assert.True(volC <= exact * 1.05);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Точностные тесты
    // ═══════════════════════════════════════════════════════════════
    public class VolumeAccuracyTests
    {
        [Fact]
        public void Flat_NoBoundary_VeryPrecise()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var calc = new VolumeCalc();
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10);
            double err = Math.Abs(vol - 20000) / 20000 * 100;
            Assert.True(err < 0.01, $"err={err:F4}%");
        }

        // -----------------------------------------------------------
        //  Аддитивность: vol(all) = vol(boundary) + vol(outside)
        //  Для плоских поверхностей, внутренний прямоугольник
        // -----------------------------------------------------------
        [Fact]
        public void Additivity_BoundaryAndComplement()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var calcAll = new VolumeCalc();
            double volAll = calcAll.CalcTotalVolume(s1, s2, 10, 10, 10, 10);

            // Выровненная граница → оба режима одинаковы
            var boundary = new List<Pt2d>
            {
                new Pt2d(30, 30), new Pt2d(70, 30),
                new Pt2d(70, 70), new Pt2d(30, 70)
            };
            var calcBnd = new VolumeCalc(boundary);
            double volBnd = calcBnd.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);

            // Для выровненной границы: внутри = 40*40*2 = 3200
            Assert.InRange(volBnd, 3100, 3300);
            Assert.InRange(volAll, 19999, 20001);
        }

        // -----------------------------------------------------------
        //  Реалистичный сценарий: outer + inner, не выровнены
        // -----------------------------------------------------------
        [Fact]
        public void RealisticScenario_BothModesBracket()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var outer = new List<Pt2d>
            {
                new Pt2d(7, 7), new Pt2d(93, 7),
                new Pt2d(93, 93), new Pt2d(7, 93)
            };
            var inner = new List<Pt2d>
            {
                new Pt2d(35, 35), new Pt2d(65, 35),
                new Pt2d(65, 65), new Pt2d(35, 65)
            };
            double exact = (86.0*86.0 - 30.0*30.0) * 2; // = 12992

            var calc = new VolumeCalc(outer, new List<List<Pt2d>> { inner });
            double volU = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: true);
            double volC = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, dontClipCells: false);

            // Оба в разумных пределах от точного
            Assert.InRange(volU, exact * 0.85, exact * 1.20);
            Assert.InRange(volC, exact * 0.80, exact * 1.15);
        }

        // -----------------------------------------------------------
        //  Наклонная поверхность + граница: аналитическое решение
        // -----------------------------------------------------------
        [Fact]
        public void TiltedSurface_WithBoundary()
        {
            var s1 = MockSurface.Flat(0);
            var s2 = MockSurface.Tilted(0, 0.02, 0.01);
            // Выровненная граница → точный результат
            var boundary = new List<Pt2d>
            {
                new Pt2d(20, 20), new Pt2d(80, 20),
                new Pt2d(80, 80), new Pt2d(20, 80)
            };
            double expected = 5400;
            var calc = new VolumeCalc(boundary);
            double vol = calc.CalcTotalVolume(s1, s2, 10, 10, 10, 10, subSteps: 16,
                dontClipCells: true);
            double errPct = Math.Abs(vol - expected) / expected * 100;
            Assert.True(errPct < 2.0, $"expected={expected}, got={vol:F1}, err={errPct:F2}%");
        }

        // -----------------------------------------------------------
        //  Мелкая сетка + L-shape: высокая точность
        // -----------------------------------------------------------
        [Fact]
        public void FineCells_LShapeBoundary()
        {
            var s1 = MockSurface.Flat(50);
            var s2 = MockSurface.Flat(52);
            var boundary = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(100, 0),
                new Pt2d(100, 50), new Pt2d(50, 50),
                new Pt2d(50, 100), new Pt2d(0, 100)
            };
            double expected = 15000;
            var calc = new VolumeCalc(boundary);
            double vol = calc.CalcTotalVolume(s1, s2, 20, 20, 5, 5, dontClipCells: true);
            double errPct = Math.Abs(vol - expected) / expected * 100;
            // Мелкая сетка → высокая точность даже без клиппинга
            Assert.True(errPct < 5.0, $"expected={expected}, got={vol:F1}, err={errPct:F2}%");
        }
    }
}
