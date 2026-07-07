using System;
using Autodesk.Civil.DatabaseServices;

namespace KartogrammaPlugin
{
    /// <summary>
    /// Снимок TIN-поверхности в память: все треугольники + равномерный
    /// пространственный индекс (CSR). Отметка в точке считается чистым C#
    /// (барицентрическая интерполяция) — без COM-вызовов FindElevationAtXY
    /// и без исключений на промахах, которые доминируют во времени расчёта
    /// объёмов. На TIN интерполяция линейна по треугольнику, поэтому значения
    /// совпадают с FindElevationAtXY. После построения объект иммутабелен —
    /// безопасен для параллельного чтения из нескольких потоков.
    /// </summary>
    internal sealed class TinSnapshot
    {
        // Вершины треугольников — плоские массивы по 1 элементу на треугольник
        private readonly double[] _ax, _ay, _az;
        private readonly double[] _bx, _by, _bz;
        private readonly double[] _cx, _cy, _cz;

        // Равномерная сетка индекса в CSR-формате:
        // ячейка k хранит треугольники _cellTris[_cellStart[k] .. _cellStart[k+1])
        private readonly int[] _cellStart;
        private readonly int[] _cellTris;
        private readonly double _minX, _minY, _step;
        private readonly int _nx, _ny;

        public int TriangleCount => _ax.Length;

        private TinSnapshot(double[] ax, double[] ay, double[] az,
                            double[] bx, double[] by, double[] bz,
                            double[] cx, double[] cy, double[] cz,
                            int[] cellStart, int[] cellTris,
                            double minX, double minY, double step, int nx, int ny)
        {
            _ax = ax; _ay = ay; _az = az;
            _bx = bx; _by = by; _bz = bz;
            _cx = cx; _cy = cy; _cz = cz;
            _cellStart = cellStart; _cellTris = cellTris;
            _minX = minX; _minY = minY; _step = step; _nx = nx; _ny = ny;
        }

        /// <summary>Построить снимок TIN-поверхности. null — нет треугольников.</summary>
        public static TinSnapshot? Build(TinSurface tin)
        {
            // ВАЖНО: только видимые треугольники (includeInvisibleTriangle=false).
            // Невидимые — скрытые границами поверхности (outer/hide boundary):
            // FindElevationAtXY на них кидает «нет поверхности», и снимок обязан
            // вести себя так же, иначе у кромок появляются фантомные отметки
            // и объёмы (свойство Triangles вернуло бы ВСЕ треугольники).
            var tris = tin.GetTriangles(false);
            int n = tris.Count;
            if (n == 0) return null;

            var ax = new double[n]; var ay = new double[n]; var az = new double[n];
            var bx = new double[n]; var by = new double[n]; var bz = new double[n];
            var cx = new double[n]; var cy = new double[n]; var cz = new double[n];

            int k = 0;
            foreach (TinSurfaceTriangle t in tris)
            {
                var p1 = t.Vertex1.Location;
                var p2 = t.Vertex2.Location;
                var p3 = t.Vertex3.Location;
                ax[k] = p1.X; ay[k] = p1.Y; az[k] = p1.Z;
                bx[k] = p2.X; by[k] = p2.Y; bz[k] = p2.Z;
                cx[k] = p3.X; cy[k] = p3.Y; cz[k] = p3.Z;
                k++;
            }
            if (k < n)
            {
                Array.Resize(ref ax, k); Array.Resize(ref ay, k); Array.Resize(ref az, k);
                Array.Resize(ref bx, k); Array.Resize(ref by, k); Array.Resize(ref bz, k);
                Array.Resize(ref cx, k); Array.Resize(ref cy, k); Array.Resize(ref cz, k);
                n = k;
            }
            if (n == 0) return null;

            return BuildFromArrays(ax, ay, az, bx, by, bz, cx, cy, cz);
        }

        /// <summary>Ядро построения индекса — отделено от AutoCAD API (тестируемо).</summary>
        internal static TinSnapshot BuildFromArrays(
            double[] ax, double[] ay, double[] az,
            double[] bx, double[] by, double[] bz,
            double[] cx, double[] cy, double[] cz)
        {
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

            // Шаг индекса ≈ размер «среднего» треугольника → O(1) кандидатов
            // на запрос. Число ячеек ограничено, чтобы не раздувать память.
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

            // CSR за два прохода: счёт → префиксные суммы → заполнение
            var cellStart = new int[nx * ny + 1];

            void CellRange(int i, out int i0, out int i1, out int j0, out int j1)
            {
                double tMinX = Math.Min(ax[i], Math.Min(bx[i], cx[i]));
                double tMaxX = Math.Max(ax[i], Math.Max(bx[i], cx[i]));
                double tMinY = Math.Min(ay[i], Math.Min(by[i], cy[i]));
                double tMaxY = Math.Max(ay[i], Math.Max(by[i], cy[i]));
                j0 = Clamp((int)((tMinX - minX) / step), 0, nx - 1);
                j1 = Clamp((int)((tMaxX - minX) / step), 0, nx - 1);
                i0 = Clamp((int)((tMinY - minY) / step), 0, ny - 1);
                i1 = Clamp((int)((tMaxY - minY) / step), 0, ny - 1);
            }

            for (int t = 0; t < n; t++)
            {
                CellRange(t, out int i0, out int i1, out int j0, out int j1);
                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    cellStart[i * nx + j + 1]++;
            }
            for (int i = 1; i < cellStart.Length; i++) cellStart[i] += cellStart[i - 1];

            var cellTris = new int[cellStart[cellStart.Length - 1]];
            var fill = new int[nx * ny];
            for (int t = 0; t < n; t++)
            {
                CellRange(t, out int i0, out int i1, out int j0, out int j1);
                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    int cell = i * nx + j;
                    cellTris[cellStart[cell] + fill[cell]++] = t;
                }
            }

            return new TinSnapshot(ax, ay, az, bx, by, bz, cx, cy, cz,
                cellStart, cellTris, minX, minY, step, nx, ny);
        }

        /// <summary>
        /// Отметка поверхности в точке (план). null — точка вне поверхности
        /// (нет накрывающего треугольника) — аналог исключения FindElevationAtXY.
        /// </summary>
        public double? Elevation(double x, double y)
        {
            int j = (int)((x - _minX) / _step);
            int i = (int)((y - _minY) / _step);
            if (i < 0 || j < 0 || i >= _ny || j >= _nx) return null;

            int cell = i * _nx + j;
            int from = _cellStart[cell], to = _cellStart[cell + 1];
            for (int q = from; q < to; q++)
            {
                int t = _cellTris[q];
                double x1 = _ax[t], y1 = _ay[t];
                double x2 = _bx[t], y2 = _by[t];
                double x3 = _cx[t], y3 = _cy[t];

                double d = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
                if (Math.Abs(d) < 1e-14) continue;          // вырожденный

                double l1 = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / d;
                double l2 = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / d;
                double l3 = 1.0 - l1 - l2;

                const double eps = -1e-9;                    // допуск на рёбрах
                if (l1 < eps || l2 < eps || l3 < eps) continue;

                return l1 * _az[t] + l2 * _bz[t] + l3 * _cz[t];
            }
            return null;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
