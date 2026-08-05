using System;
using System.Collections.Generic;

namespace KartogrammaPlugin
{
    /// <summary>
    /// Обрезка ОДНОЙ ячейки сетки по «автоматической границе» — фактическому
    /// контуру зоны данных (там, где заданы обе поверхности). Используется в
    /// режиме «Границы автоматически», когда снят флаг «Не обрезать сетку
    /// квадратов».
    ///
    /// Контур внутри ячейки строится алгоритмом marching squares по булевой
    /// выборке предиката «есть данные»; точки пересечения уточняются бинарным
    /// поиском, поэтому край получается гладким, а не ступенчатым.
    ///
    /// ВАЖНО: обрезка ЧИСТО ВИЗУАЛЬНАЯ. Объёмы, площади и подписи отметок
    /// считаются другими методами по своим критериям — этот модуль их не
    /// затрагивает и в расчётах не участвует.
    ///
    /// Модуль намеренно не зависит от типов AutoCAD/Civil: он подключается
    /// к юнит-тестам напрямую (см. KartogrammaTests.csproj).
    /// </summary>
    internal static class CellClipper
    {
        internal enum CellFill
        {
            /// <summary>Данных в ячейке нет — рисовать нечего.</summary>
            Empty,
            /// <summary>Ячейка целиком в зоне данных — рисуется обычный квадрат.</summary>
            Full,
            /// <summary>Ячейка на краю зоны — рисуются обрезанные контуры.</summary>
            Partial,
        }

        /// <summary>
        /// Построить контуры зоны данных внутри ячейки.
        /// </summary>
        /// <param name="szX">Размер ячейки по X (локальные координаты сетки).</param>
        /// <param name="szY">Размер ячейки по Y.</param>
        /// <param name="steps">Число интервалов выборки по стороне (узлов steps+1).</param>
        /// <param name="refineSteps">Итераций бисекции при уточнении точки на краю.</param>
        /// <param name="hasData">Предикат «заданы обе поверхности» в локальных
        /// координатах ячейки: (0,0) — левый нижний угол, (szX, szY) — правый верхний.</param>
        /// <param name="loops">Замкнутые контуры (без повтора первой точки в конце).</param>
        internal static CellFill Clip(
            double szX, double szY, int steps, int refineSteps,
            Func<double, double, bool> hasData,
            out List<List<(double X, double Y)>> loops)
        {
            loops = new List<List<(double X, double Y)>>();

            int n = Math.Max(2, steps);
            double dx = szX / n, dy = szY / n;

            // ── Булева выборка узлов (n+1)×(n+1): i — по Y, j — по X ──────────
            var b = new bool[n + 1, n + 1];
            int inCount = 0;
            for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                bool v = hasData(j * dx, i * dy);
                b[i, j] = v;
                if (v) inCount++;
            }

            if (inCount == 0) return CellFill.Empty;
            if (inCount == (n + 1) * (n + 1)) return CellFill.Full;

            // ── Точки пересечения контура с рёбрами выборки ───────────────────
            //   hx[i, j] — X точки на горизонтальном ребре (i,j)→(i,j+1), Y = i*dy
            //   vy[i, j] — Y точки на вертикальном  ребре (i,j)→(i+1,j), X = j*dx
            var hx = new double[n + 1, n];
            var vy = new double[n, n + 1];
            for (int i = 0; i <= n; i++)
            for (int j = 0; j < n; j++)
                if (b[i, j] != b[i, j + 1])
                    hx[i, j] = BisectX(hasData, i * dy, j * dx, (j + 1) * dx, b[i, j], refineSteps);
            for (int i = 0; i < n; i++)
            for (int j = 0; j <= n; j++)
                if (b[i, j] != b[i + 1, j])
                    vy[i, j] = BisectY(hasData, j * dx, i * dy, (i + 1) * dy, b[i, j], refineSteps);

            // ── Сборка направленных отрезков контура ──────────────────────────
            // Каждая вершина адресуется ЦЕЛЫМ ключом (узел выборки / точка на
            // горизонтальном ребре / точка на вертикальном ребре) — стыковка
            // отрезков идёт по точному совпадению ключей, без допусков.
            long stride = n + 2;
            long Key(int kind, int i, int j) => (kind * stride + i) * stride + j;

            var pos  = new Dictionary<long, (double X, double Y)>();
            var next = new Dictionary<long, long>();
            bool bad = false;

            void Seg(long ka, (double X, double Y) pa, long kb, (double X, double Y) pb)
            {
                pos[ka] = pa;
                pos[kb] = pb;
                if (next.ContainsKey(ka)) { bad = true; return; }
                next[ka] = kb;
            }

            // Направление отрезков выбрано так, чтобы зона данных всегда была
            // СЛЕВА по ходу обхода — тогда отрезки стыкуются в замкнутые контуры.
            for (int i = 0; i < n && !bad; i++)
            for (int j = 0; j < n && !bad; j++)
            {
                bool bl = b[i, j], br = b[i, j + 1], tr = b[i + 1, j + 1], tl = b[i + 1, j];
                int cs = (bl ? 1 : 0) | (br ? 2 : 0) | (tr ? 4 : 0) | (tl ? 8 : 0);
                if (cs == 0 || cs == 15) continue;

                long kB = Key(1, i,     j);      // низ
                long kT = Key(1, i + 1, j);      // верх
                long kL = Key(2, i,     j);      // лево
                long kR = Key(2, i,     j + 1);  // право
                var pB = (hx[i, j],     i * dy);
                var pT = (hx[i + 1, j], (i + 1) * dy);
                var pL = (j * dx,       vy[i, j]);
                var pR = ((j + 1) * dx, vy[i, j + 1]);

                switch (cs)
                {
                    case 1:  Seg(kB, pB, kL, pL); break;  // внутри только bl
                    case 2:  Seg(kR, pR, kB, pB); break;  // внутри только br
                    case 4:  Seg(kT, pT, kR, pR); break;  // внутри только tr
                    case 8:  Seg(kL, pL, kT, pT); break;  // внутри только tl
                    case 3:  Seg(kR, pR, kL, pL); break;  // внутри нижняя половина
                    case 12: Seg(kL, pL, kR, pR); break;  // внутри верхняя половина
                    case 6:  Seg(kT, pT, kB, pB); break;  // внутри правая половина
                    case 9:  Seg(kB, pB, kT, pT); break;  // внутри левая половина
                    case 14: Seg(kL, pL, kB, pB); break;  // снаружи только bl
                    case 13: Seg(kB, pB, kR, pR); break;  // снаружи только br
                    case 11: Seg(kR, pR, kT, pT); break;  // снаружи только tr
                    case 7:  Seg(kT, pT, kL, pL); break;  // снаружи только tl
                    // Седловые случаи: диагональные пары. Что с чем соединять,
                    // решает проба в центре квадратика.
                    case 5:
                        if (hasData((j + 0.5) * dx, (i + 0.5) * dy))
                             { Seg(kR, pR, kB, pB); Seg(kT, pT, kL, pL); }
                        else { Seg(kB, pB, kL, pL); Seg(kT, pT, kR, pR); }
                        break;
                    case 10:
                        if (hasData((j + 0.5) * dx, (i + 0.5) * dy))
                             { Seg(kL, pL, kB, pB); Seg(kR, pR, kT, pT); }
                        else { Seg(kR, pR, kB, pB); Seg(kL, pL, kT, pT); }
                        break;
                }
            }

            // ── Отрезки по кромке самой ячейки ────────────────────────────────
            // Обход против часовой стрелки (низ → право → верх → лево): при
            // таком направлении нутро ячейки тоже остаётся слева, и кромочные
            // отрезки стыкуются с отрезками контура в общих точках.
            var ring = new List<(int I, int J)>(4 * n);
            for (int j = 0; j < n; j++) ring.Add((0, j));
            for (int i = 0; i < n; i++) ring.Add((i, n));
            for (int j = n; j > 0; j--) ring.Add((n, j));
            for (int i = n; i > 0; i--) ring.Add((i, 0));

            for (int k = 0; k < ring.Count && !bad; k++)
            {
                var (ai, aj) = ring[k];
                var (bi, bj) = ring[(k + 1) % ring.Count];
                bool aIn = b[ai, aj], bIn = b[bi, bj];
                if (!aIn && !bIn) continue;

                long ka = Key(0, ai, aj); var pa = (aj * dx, ai * dy);
                long kb = Key(0, bi, bj); var pb = (bj * dx, bi * dy);

                if (aIn && bIn) { Seg(ka, pa, kb, pb); continue; }

                long kc; (double X, double Y) pc;
                if (ai == bi)
                {
                    int jj = Math.Min(aj, bj);
                    kc = Key(1, ai, jj); pc = (hx[ai, jj], ai * dy);
                }
                else
                {
                    int ii = Math.Min(ai, bi);
                    kc = Key(2, ii, aj); pc = (aj * dx, vy[ii, aj]);
                }

                if (aIn) Seg(ka, pa, kc, pc);
                else     Seg(kc, pc, kb, pb);
            }

            // ── Стыковка отрезков в замкнутые контуры ─────────────────────────
            // У корректной конфигурации каждый ключ имеет ровно один вход и один
            // выход, поэтому обход от любой непосещённой вершины даёт цикл.
            double simplifyTol = Math.Min(Math.Min(szX, szY) * 1e-3, 1e-3);
            double minArea     = szX * szY * 1e-6;

            if (!bad)
            {
                var visited = new HashSet<long>();
                foreach (var startKey in next.Keys)
                {
                    if (visited.Contains(startKey)) continue;

                    var loop = new List<(double X, double Y)>();
                    long cur = startKey;
                    int guard = next.Count + 1;
                    while (guard-- > 0)
                    {
                        if (!visited.Add(cur)) break;
                        loop.Add(pos[cur]);
                        if (!next.TryGetValue(cur, out long nx)) { bad = true; break; }
                        cur = nx;
                        if (cur == startKey) break;
                    }
                    if (bad) break;

                    var simp = Simplify(loop, simplifyTol);
                    if (simp.Count >= 3 && Math.Abs(Area(simp)) > minArea)
                        loops.Add(simp);
                }
            }

            // Вырожденная конфигурация (стыковка не сошлась) — не выдумываем
            // геометрию: пусть вызывающий код нарисует обычный целый квадрат,
            // как в режиме «не обрезать». Хуже, чем было, не станет.
            if (bad || loops.Count == 0)
            {
                loops.Clear();
                return CellFill.Full;
            }

            return CellFill.Partial;
        }

        // ── Бисекция до края зоны данных ──────────────────────────────────────

        private static double BisectX(Func<double, double, bool> hasData, double y,
            double xA, double xB, bool aInside, int steps)
        {
            double xIn = aInside ? xA : xB, xOut = aInside ? xB : xA;
            for (int k = 0; k < steps; k++)
            {
                double m = (xIn + xOut) * 0.5;
                if (hasData(m, y)) xIn = m; else xOut = m;
            }
            return (xIn + xOut) * 0.5;
        }

        private static double BisectY(Func<double, double, bool> hasData, double x,
            double yA, double yB, bool aInside, int steps)
        {
            double yIn = aInside ? yA : yB, yOut = aInside ? yB : yA;
            for (int k = 0; k < steps; k++)
            {
                double m = (yIn + yOut) * 0.5;
                if (hasData(x, m)) yIn = m; else yOut = m;
            }
            return (yIn + yOut) * 0.5;
        }

        // ── Чистка контура ────────────────────────────────────────────────────

        /// <summary>Убрать совпадающие и лежащие на одной прямой вершины —
        /// длинные прямые участки кромки схлопываются в один отрезок.</summary>
        internal static List<(double X, double Y)> Simplify(
            List<(double X, double Y)> pts, double tol)
        {
            var a = new List<(double X, double Y)>(pts.Count);
            foreach (var p in pts)
                if (a.Count == 0 || !Same(p, a[a.Count - 1])) a.Add(p);
            if (a.Count > 1 && Same(a[0], a[a.Count - 1])) a.RemoveAt(a.Count - 1);
            if (a.Count < 3) return a;

            bool changed = true;
            while (changed && a.Count > 3)
            {
                changed = false;
                for (int i = 0; i < a.Count && a.Count > 3; i++)
                {
                    var p0 = a[(i - 1 + a.Count) % a.Count];
                    var p1 = a[i];
                    var p2 = a[(i + 1) % a.Count];
                    if (PerpDist(p1, p0, p2) <= tol) { a.RemoveAt(i); i--; changed = true; }
                }
            }
            return a;
        }

        private static bool Same((double X, double Y) p, (double X, double Y) q) =>
            Math.Abs(p.X - q.X) < 1e-12 && Math.Abs(p.Y - q.Y) < 1e-12;

        private static double PerpDist((double X, double Y) p,
            (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-24)
            {
                double ex = p.X - a.X, ey = p.Y - a.Y;
                return Math.Sqrt(ex * ex + ey * ey);
            }
            double cross = Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx);
            return cross / Math.Sqrt(len2);
        }

        /// <summary>Площадь замкнутого контура (со знаком, формула шнурков).</summary>
        internal static double Area(List<(double X, double Y)> p)
        {
            double s = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                s += p[j].X * p[i].Y - p[i].X * p[j].Y;
            return s * 0.5;
        }
    }
}
