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
    /// берутся ЛИНЕЙНОЙ интерполяцией между узлами выборки: рабочая отметка —
    /// непрерывная величина, и на отрезке между узлами она считается линейной
    /// ровно так же, как при расчёте объёма. Поэтому линия на чертеже проходит
    /// там же, где проходит граница разделения объёмов, — цифры и картинка не
    /// расходятся.
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
            double szX, double szY, int steps, Func<double, double, double> height)
        {
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

            // Точка h = 0 на ребре между значениями a и b разных знаков.
            static double Cross(double a, double b) => a / (a - b);

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

                var pB = ((j + Cross(bl, br)) * dx, i * dy);
                var pT = ((j + Cross(tl, tr)) * dx, (i + 1) * dy);
                var pL = (j * dx,       (i + Cross(bl, tl)) * dy);
                var pR = ((j + 1) * dx, (i + Cross(br, tr)) * dy);

                switch (cs)
                {
                    case 1: case 14: Link(kB, pB, kL, pL); break;
                    case 2: case 13: Link(kR, pR, kB, pB); break;
                    case 4: case 11: Link(kT, pT, kR, pR); break;
                    case 8: case 7:  Link(kL, pL, kT, pT); break;
                    case 3: case 12: Link(kR, pR, kL, pL); break;
                    case 6: case 9:  Link(kT, pT, kB, pB); break;

                    // Седловые случаи: как соединять диагональные пары, решает
                    // значение в центре квадратика (билинейное среднее).
                    case 5:
                        if ((bl + br + tr + tl) > 0)
                             { Link(kR, pR, kB, pB); Link(kT, pT, kL, pL); }
                        else { Link(kB, pB, kL, pL); Link(kT, pT, kR, pR); }
                        break;
                    case 10:
                        if ((bl + br + tr + tl) > 0)
                             { Link(kL, pL, kB, pB); Link(kR, pR, kT, pT); }
                        else { Link(kR, pR, kB, pB); Link(kL, pL, kT, pT); }
                        break;
                }
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
