using System;
using System.Collections.Generic;

namespace KartogrammaPlugin
{
    /// <summary>
    /// Трассировка ЛИНИИ НУЛЕВЫХ РАБОТ внутри одной ячейки сетки — контура,
    /// где рабочая отметка обращается в ноль, то есть проектная поверхность
    /// пересекает существующую. Именно эта линия делит квадрат на насыпную и
    /// выемочную части (см. ZeroLineSplit) и ограничивает штриховки.
    ///
    /// Алгоритм — marching squares по знаку рабочей отметки. Точки пересечения
    /// уточняются бисекцией по реальной отметке (как край штриховки в
    /// CellClipper), седловые квадратики решаются по реальной отметке в
    /// центре — поэтому линия совпадает с краем штриховки, а не с линейной
    /// интерполяцией между узлами выборки, которая врёт там, где между узлами
    /// проходит ребро треугольника TIN.
    ///
    /// Узлы без данных (NaN) обрывают контур: там работ нет.
    ///
    /// Модуль не зависит от типов AutoCAD/Civil — подключается к тестам.
    /// </summary>
    internal static class ZeroContour
    {
        /// <summary>
        /// Ломаные линии h = 0 внутри ячейки, в локальных координатах ячейки
        /// (0,0) — левый нижний угол, (szX, szY) — правый верхний.
        /// </summary>
        /// <param name="szX">Размер ячейки по X.</param>
        /// <param name="szY">Размер ячейки по Y.</param>
        /// <param name="steps">Число интервалов выборки по стороне.</param>
        /// <param name="height">Рабочая отметка в точке; NaN — данных нет.</param>
        internal static List<List<(double X, double Y)>> Trace(
            double szX, double szY, int steps, Func<double, double, double> height,
            double exactTol = 0)
        {
            // Сторона нулевой линии для точного уточнения (как у штриховки)
            int Side(double x, double y)
            {
                double hv = height(x, y);
                return double.IsNaN(hv) ? 0 : hv > 0 ? 1 : -1;
            }

            var result = new List<List<(double X, double Y)>>();

            int n = Math.Max(2, steps);
            double dx = szX / n, dy = szY / n;

            var v = new double[n + 1, n + 1];
            bool anyPos = false, anyNeg = false;
            for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                double hv = height(j * dx, i * dy);
                v[i, j] = hv;
                if (double.IsNaN(hv)) continue;
                if (hv > 0) anyPos = true; else if (hv < 0) anyNeg = true;
            }

            // Нулевая линия существует только там, где есть обе стороны.
            if (!anyPos || !anyNeg) return result;

            long stride = n + 2;
            long Key(int kind, int i, int j) => (kind * stride + i) * stride + j;

            var pos   = new Dictionary<long, (double X, double Y)>();
            var adj   = new Dictionary<long, List<long>>();

            void Link(long ka, (double X, double Y) pa, long kb, (double X, double Y) pb)
            {
                pos[ka] = pa; pos[kb] = pb;
                if (!adj.TryGetValue(ka, out var la)) adj[ka] = la = new List<long>();
                if (!adj.TryGetValue(kb, out var lb)) adj[kb] = lb = new List<long>();
                la.Add(kb);
                lb.Add(ka);
            }

            // Точка h = 0 на ребре между значениями a и b разных знаков — доля
            // ребра от его начала. Уточняется по РЕАЛЬНОЙ отметке бисекцией,
            // как край штриховки в CellClipper: между узлами выборки может
            // пройти ребро треугольника TIN, и линейная интерполяция по двум
            // концам уводила линию на сантиметры от края штриховки (на мелких
            // пятнах штриховка «вылезала» за линию). Остаток интервала после
            // бисекции — линейно. Каждое ребро считается один раз, поэтому
            // соседние квадратики получают одну и ту же точку.
            const int Refine = KartogrammaPlugin.CellClipper.EdgeRefineSteps;
            var cross = new Dictionary<long, double>();
            double Cross(long key, double a, double b, double x0, double y0, double ex, double ey)
            {
                if (cross.TryGetValue(key, out double t)) return t;
                double lo = 0, hi = 1, hLo = a, hHi = b;
                for (int k = 0; k < Refine; k++)
                {
                    double m = (lo + hi) * 0.5;
                    double hm = height(x0 + ex * m, y0 + ey * m);
                    if (double.IsNaN(hm)) break;
                    if ((hm > 0) == (hLo > 0)) { lo = m; hLo = hm; }
                    else                       { hi = m; hHi = hm; }
                }
                t = lo + (hi - lo) * hLo / (hLo - hHi);
                cross[key] = t;
                return t;
            }

            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double bl = v[i, j], br = v[i, j + 1];
                double tr = v[i + 1, j + 1], tl = v[i + 1, j];

                // Хотя бы один угол без данных — контур в этом квадратике
                // не определён, обрываем: там работ нет.
                if (double.IsNaN(bl) || double.IsNaN(br) ||
                    double.IsNaN(tr) || double.IsNaN(tl)) continue;

                int cs = (bl > 0 ? 1 : 0) | (br > 0 ? 2 : 0)
                       | (tr > 0 ? 4 : 0) | (tl > 0 ? 8 : 0);
                if (cs == 0 || cs == 15) continue;

                long kB = Key(1, i,     j);
                long kT = Key(1, i + 1, j);
                long kL = Key(2, i,     j);
                long kR = Key(2, i,     j + 1);

                // Точки считаются только на рёбрах со сменой знака — их и
                // использует case ниже; на остальных рёбрах не вызываются.
                (double X, double Y) pB = default, pT = default, pL = default, pR = default;
                if ((bl > 0) != (br > 0))
                    pB = ((j + Cross(kB, bl, br, j * dx, i * dy, dx, 0)) * dx, i * dy);
                if ((tl > 0) != (tr > 0))
                    pT = ((j + Cross(kT, tl, tr, j * dx, (i + 1) * dy, dx, 0)) * dx, (i + 1) * dy);
                if ((bl > 0) != (tl > 0))
                    pL = (j * dx, (i + Cross(kL, bl, tl, j * dx, i * dy, 0, dy)) * dy);
                if ((br > 0) != (tr > 0))
                    pR = ((j + 1) * dx, (i + Cross(kR, br, tr, (j + 1) * dx, i * dy, 0, dy)) * dy);

                switch (cs)
                {
                    case 1: case 14: Link(kB, pB, kL, pL); break;
                    case 2: case 13: Link(kR, pR, kB, pB); break;
                    case 4: case 11: Link(kT, pT, kR, pR); break;
                    case 8: case 7:  Link(kL, pL, kT, pT); break;
                    case 3: case 12: Link(kR, pR, kL, pL); break;
                    case 6: case 9:  Link(kT, pT, kB, pB); break;

                    // Седловые случаи: как соединять диагональные пары, решает
                    // РЕАЛЬНАЯ отметка в центре квадратика — так же, как при
                    // обрезке штриховки (CellClipper). Билинейное среднее углов
                    // здесь врёт: тонкая перемычка насыпи, проходящая между
                    // узлами выборки, по среднему «рвётся», и линия нулевых
                    // работ отрезала островок, который штриховка (по
                    // поверхностям) верно соединяла с соседней насыпью.
                    case 5:
                        if (MidPositive(i, j, bl, br, tr, tl))
                             { Link(kR, pR, kB, pB); Link(kT, pT, kL, pL); }
                        else { Link(kB, pB, kL, pL); Link(kT, pT, kR, pR); }
                        break;
                    case 10:
                        if (MidPositive(i, j, bl, br, tr, tl))
                             { Link(kL, pL, kB, pB); Link(kR, pR, kT, pT); }
                        else { Link(kR, pR, kB, pB); Link(kL, pL, kT, pT); }
                        break;
                }
            }

            bool MidPositive(int i, int j, double bl, double br, double tr, double tl)
            {
                double mid = height((j + 0.5) * dx, (i + 0.5) * dy);
                if (double.IsNaN(mid)) mid = (bl + br + tr + tl) / 4.0;
                return mid > 0;
            }

            if (adj.Count == 0) return result;

            // ── Сшивка отрезков в ломаные ─────────────────────────────────────
            // Точка на ребре выборки принадлежит максимум двум квадратикам,
            // поэтому степень вершины не больше двух и цепочки простые.
            var used = new HashSet<(long, long)>();
            bool Take(long a, long b)
                => used.Add(a < b ? (a, b) : (b, a));

            // Сначала от концов (степень 1) — получаются открытые ломаные,
            // затем то, что осталось, — замкнутые контуры внутри ячейки.
            foreach (var pass in new[] { 1, 2 })
            {
                foreach (var start in adj.Keys)
                {
                    if (pass == 1 && adj[start].Count != 1) continue;

                    while (true)
                    {
                        long? next = null;
                        foreach (var nb in adj[start])
                            if (!used.Contains(start < nb ? (start, nb) : (nb, start)))
                            { next = nb; break; }
                        if (next == null) break;

                        var chain = new List<(double X, double Y)> { pos[start] };
                        long cur = start, nxt = next.Value;
                        int guard = adj.Count * 2 + 4;

                        while (guard-- > 0)
                        {
                            if (!Take(cur, nxt)) break;
                            if (exactTol > 0)
                                CellClipper.RefineBoundary(Side, pos[cur], pos[nxt],
                                    exactTol, szX, szY, chain,
                                    reach: Math.Sqrt(dx * dx + dy * dy));
                            chain.Add(pos[nxt]);

                            long? cont = null;
                            foreach (var nb in adj[nxt])
                                if (!used.Contains(nxt < nb ? (nxt, nb) : (nb, nxt)))
                                { cont = nb; break; }
                            if (cont == null) break;

                            cur = nxt;
                            nxt = cont.Value;
                        }

                        if (chain.Count >= 2) result.Add(Simplify(chain));
                    }
                }
            }

            return result;
        }

        /// <summary>Убрать вершины, лежащие на одной прямой с соседями —
        /// длинные прямые участки нулевой линии станут одним отрезком.</summary>
        private static List<(double X, double Y)> Simplify(List<(double X, double Y)> pts)
        {
            if (pts.Count < 3) return pts;

            var res = new List<(double X, double Y)> { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var a = res[res.Count - 1];
                var b = pts[i];
                var c = pts[i + 1];

                double ux = b.X - a.X, uy = b.Y - a.Y;
                double wx = c.X - a.X, wy = c.Y - a.Y;
                double len = Math.Sqrt(wx * wx + wy * wy);
                if (len < 1e-12) continue;

                double dist = Math.Abs(ux * wy - uy * wx) / len;
                if (dist > 1e-6) res.Add(b);
            }
            res.Add(pts[pts.Count - 1]);
            return res;
        }
    }
}
