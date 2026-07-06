using System;
using System.Collections.Generic;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Ядро TinSnapshot (KartogrammaPlugin.TinSnapshot): CSR-индекс по
    //  равномерной сетке + барицентрическая интерполяция. Снимок заменяет
    //  COM-вызовы FindElevationAtXY при расчёте объёмов, поэтому значения
    //  обязаны совпадать с линейной TIN-интерполяцией, а промахи (точка
    //  вне поверхности) — возвращать null, как исключение у Civil API.
    // ═══════════════════════════════════════════════════════════════
    public class TinSnapshotTests
    {
        // ── Копия ядра TinSnapshot (без AutoCAD-зависимого Build) ─────────
        private sealed class Snap
        {
            private readonly double[] _ax, _ay, _az, _bx, _by, _bz, _cx, _cy, _cz;
            private readonly int[] _cellStart, _cellTris;
            private readonly double _minX, _minY, _step;
            private readonly int _nx, _ny;

            public Snap(double[] ax, double[] ay, double[] az,
                        double[] bx, double[] by, double[] bz,
                        double[] cx, double[] cy, double[] cz)
            {
                _ax = ax; _ay = ay; _az = az;
                _bx = bx; _by = by; _bz = bz;
                _cx = cx; _cy = cy; _cz = cz;
                int n = ax.Length;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                for (int i = 0; i < n; i++)
                {
                    minX = Math.Min(minX, Math.Min(ax[i], Math.Min(bx[i], cx[i])));
                    minY = Math.Min(minY, Math.Min(ay[i], Math.Min(by[i], cy[i])));
                    maxX = Math.Max(maxX, Math.Max(ax[i], Math.Max(bx[i], cx[i])));
                    maxY = Math.Max(maxY, Math.Max(ay[i], Math.Max(by[i], cy[i])));
                }
                double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);
                double step = Math.Max(Math.Sqrt(w * h / n), 1e-9);
                int nx = (int)Math.Ceiling(w / step), ny = (int)Math.Ceiling(h / step);
                const int MaxCells = 4_000_000;
                if ((long)nx * ny > MaxCells)
                {
                    double scale = Math.Sqrt((double)nx * ny / MaxCells);
                    step *= scale;
                    nx = (int)Math.Ceiling(w / step);
                    ny = (int)Math.Ceiling(h / step);
                }
                nx = Math.Max(nx, 1); ny = Math.Max(ny, 1);
                _minX = minX; _minY = minY; _step = step; _nx = nx; _ny = ny;

                _cellStart = new int[nx * ny + 1];

                void CellRange(int i, out int i0, out int i1, out int j0, out int j1)
                {
                    double tMinX = Math.Min(ax[i], Math.Min(bx[i], cx[i]));
                    double tMaxX = Math.Max(ax[i], Math.Max(bx[i], cx[i]));
                    double tMinY = Math.Min(ay[i], Math.Min(by[i], cy[i]));
                    double tMaxY = Math.Max(ay[i], Math.Max(by[i], cy[i]));
                    j0 = Math.Clamp((int)((tMinX - minX) / step), 0, nx - 1);
                    j1 = Math.Clamp((int)((tMaxX - minX) / step), 0, nx - 1);
                    i0 = Math.Clamp((int)((tMinY - minY) / step), 0, ny - 1);
                    i1 = Math.Clamp((int)((tMaxY - minY) / step), 0, ny - 1);
                }

                for (int t = 0; t < n; t++)
                {
                    CellRange(t, out int i0, out int i1, out int j0, out int j1);
                    for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                        _cellStart[i * nx + j + 1]++;
                }
                for (int i = 1; i < _cellStart.Length; i++) _cellStart[i] += _cellStart[i - 1];

                _cellTris = new int[_cellStart[_cellStart.Length - 1]];
                var fill = new int[nx * ny];
                for (int t = 0; t < n; t++)
                {
                    CellRange(t, out int i0, out int i1, out int j0, out int j1);
                    for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                    {
                        int cell = i * nx + j;
                        _cellTris[_cellStart[cell] + fill[cell]++] = t;
                    }
                }
            }

            public double? Elevation(double x, double y)
            {
                int j = (int)((x - _minX) / _step);
                int i = (int)((y - _minY) / _step);
                if (i < 0 || j < 0 || i >= _ny || j >= _nx) return null;

                int cell = i * _nx + j;
                for (int q = _cellStart[cell]; q < _cellStart[cell + 1]; q++)
                {
                    int t = _cellTris[q];
                    double x1 = _ax[t], y1 = _ay[t];
                    double x2 = _bx[t], y2 = _by[t];
                    double x3 = _cx[t], y3 = _cy[t];

                    double d = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
                    if (Math.Abs(d) < 1e-14) continue;

                    double l1 = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / d;
                    double l2 = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / d;
                    double l3 = 1.0 - l1 - l2;

                    const double eps = -1e-9;
                    if (l1 < eps || l2 < eps || l3 < eps) continue;

                    return l1 * _az[t] + l2 * _bz[t] + l3 * _cz[t];
                }
                return null;
            }
        }

        // Триангулированная плоскость z = 2x + 3y + 5 над сеткой M×M квадратов
        private static Snap PlaneSnap(int m, out Func<double, double, double> plane)
        {
            plane = (x, y) => 2 * x + 3 * y + 5;
            var f = plane;
            var ax = new List<double>(); var ay = new List<double>(); var az = new List<double>();
            var bx = new List<double>(); var by = new List<double>(); var bz = new List<double>();
            var cx = new List<double>(); var cy = new List<double>(); var cz = new List<double>();

            void Tri((double x, double y) p1, (double x, double y) p2, (double x, double y) p3)
            {
                ax.Add(p1.x); ay.Add(p1.y); az.Add(f(p1.x, p1.y));
                bx.Add(p2.x); by.Add(p2.y); bz.Add(f(p2.x, p2.y));
                cx.Add(p3.x); cy.Add(p3.y); cz.Add(f(p3.x, p3.y));
            }

            for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
            {
                Tri((j, i), (j + 1, i), (j, i + 1));
                Tri((j + 1, i), (j + 1, i + 1), (j, i + 1));
            }
            return new Snap(ax.ToArray(), ay.ToArray(), az.ToArray(),
                            bx.ToArray(), by.ToArray(), bz.ToArray(),
                            cx.ToArray(), cy.ToArray(), cz.ToArray());
        }

        [Fact]
        public void PlaneSurface_InterpolatesExactly_EverywhereInside()
        {
            var snap = PlaneSnap(10, out var plane);   // 200 треугольников на [0..10]²
            var rnd = new Random(42);
            for (int k = 0; k < 500; k++)
            {
                double x = rnd.NextDouble() * 10, y = rnd.NextDouble() * 10;
                var z = snap.Elevation(x, y);
                Assert.True(z.HasValue, $"промах внутри поверхности в ({x:F3},{y:F3})");
                Assert.Equal(plane(x, y), z!.Value, 9);
            }
        }

        [Fact]
        public void PointsOutsideSurface_ReturnNull()
        {
            var snap = PlaneSnap(10, out _);
            Assert.Null(snap.Elevation(-0.5, 5));      // левее
            Assert.Null(snap.Elevation(10.5, 5));      // правее
            Assert.Null(snap.Elevation(5, -3));        // ниже
            Assert.Null(snap.Elevation(100, 100));     // далеко
        }

        [Fact]
        public void VerticesAndEdges_AreHit()
        {
            var snap = PlaneSnap(4, out var plane);
            // Узлы триангуляции и середины рёбер — допуск eps должен их принимать
            Assert.Equal(plane(2, 2),     snap.Elevation(2, 2)!.Value,     9);
            Assert.Equal(plane(2.5, 2),   snap.Elevation(2.5, 2)!.Value,   9);
            Assert.Equal(plane(0, 0),     snap.Elevation(0, 0)!.Value,     9);
            Assert.Equal(plane(2.5, 2.5), snap.Elevation(2.5, 2.5)!.Value, 9);
        }

        [Fact]
        public void SurfaceWithHole_MissesInsideHole()
        {
            // Кольцо из треугольников: квадрат [0..3]² без центральной ячейки [1..2]²
            var f = (double x, double y) => x + y;
            var ax = new List<double>(); var ay = new List<double>(); var az = new List<double>();
            var bx = new List<double>(); var by = new List<double>(); var bz = new List<double>();
            var cx = new List<double>(); var cy = new List<double>(); var cz = new List<double>();
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                if (i == 1 && j == 1) continue;        // «дырка»
                ax.Add(j); ay.Add(i); az.Add(f(j, i));
                bx.Add(j + 1); by.Add(i); bz.Add(f(j + 1, i));
                cx.Add(j); cy.Add(i + 1); cz.Add(f(j, i + 1));
                ax.Add(j + 1); ay.Add(i); az.Add(f(j + 1, i));
                bx.Add(j + 1); by.Add(i + 1); bz.Add(f(j + 1, i + 1));
                cx.Add(j); cy.Add(i + 1); cz.Add(f(j, i + 1));
            }
            var snap = new Snap(ax.ToArray(), ay.ToArray(), az.ToArray(),
                                bx.ToArray(), by.ToArray(), bz.ToArray(),
                                cx.ToArray(), cy.ToArray(), cz.ToArray());

            Assert.Null(snap.Elevation(1.5, 1.5));                 // в дырке — null
            Assert.Equal(1.0, snap.Elevation(0.5, 0.5)!.Value, 9); // в кольце — значение
        }
    }
}
