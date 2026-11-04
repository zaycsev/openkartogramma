using System;
using System.Collections.Generic;

namespace KartogrammaPlugin
{
    /// <summary>
    /// ТОЧНОЕ разделение ячейки на насыпь и выемку по двум TIN-поверхностям.
    ///
    /// Каждая поверхность — набор плоских треугольников. Внутри пересечения
    /// треугольника одной поверхности с треугольником другой (выпуклый
    /// многоугольник — «кусок») обе отметки линейны, значит линейна и рабочая
    /// отметка h = Z2 − Z1. Нулевая линия в куске — точный отрезок, части
    /// насыпи и выемки — выпуклые многоугольники. Склейка кусков даёт контуры
    /// штриховки и линию нулевых работ без выборки: их точность — точность
    /// арифметики (доли микрона), и никакое узкое пятно не проскакивает
    /// между узлами. Попутно — точные площади и объёмы насыпи и выемки.
    ///
    /// Координаты — локальные координаты ячейки: (0,0) — левый нижний угол,
    /// (szX, szY) — правый верхний. Модуль не зависит от AutoCAD/Civil —
    /// подключается к тестам.
    /// </summary>
    internal static class TinOverlay
    {
        /// <summary>Треугольник поверхности в локальных координатах ячейки.</summary>
        internal readonly struct Tri
        {
            public readonly double X1, Y1, Z1, X2, Y2, Z2, X3, Y3, Z3;
            public Tri(double x1, double y1, double z1, double x2, double y2, double z2,
                       double x3, double y3, double z3)
            {
                X1 = x1; Y1 = y1; Z1 = z1; X2 = x2; Y2 = y2; Z2 = z2; X3 = x3; Y3 = y3; Z3 = z3;
            }
        }

        internal sealed class Result
        {
            /// <summary>Контуры насыпи / выемки: внешние — против часовой,
            /// дырки — по часовой.</summary>
            public List<List<(double X, double Y)>> Fill = new(), Cut = new();
            /// <summary>Ломаные линии нулевых работ.</summary>
            public List<List<(double X, double Y)>> Zero = new();
            public double FillArea, CutArea, FillVolume, CutVolume;
            /// <summary>Площадь, где заданы обе поверхности (включая h = 0).</summary>
            public double Area;
        }

        // Пространственный допуск склейки вершин соседних кусков, м.
        private const double Eps = 1e-9;

        /// <summary>
        /// Разделить ячейку. null — склейка не сошлась (вырожденная геометрия):
        /// вызывающий код возвращается к расчёту по выборке.
        /// </summary>
        internal static Result? Build(double szX, double szY,
            IReadOnlyList<Tri> t1, IReadOnlyList<Tri> t2)
        {
            var res = new Result();
            var fillPieces = new List<List<(double X, double Y)>>();
            var cutPieces  = new List<List<(double X, double Y)>>();
            var zeroSegs   = new List<((double X, double Y) A, (double X, double Y) B)>();

            // Габариты треугольников второй поверхности и сетка-индекс по ячейке:
            // каждому треугольнику первой — только соседи, а не вся вторая TIN.
            var b2 = new (double x0, double y0, double x1, double y1)[t2.Count];
            for (int k = 0; k < t2.Count; k++)
            {
                var t = t2[k];
                b2[k] = (Math.Min(t.X1, Math.Min(t.X2, t.X3)), Math.Min(t.Y1, Math.Min(t.Y2, t.Y3)),
                         Math.Max(t.X1, Math.Max(t.X2, t.X3)), Math.Max(t.Y1, Math.Max(t.Y2, t.Y3)));
            }
            int gn = Math.Max(1, Math.Min(64, (int)Math.Sqrt(t2.Count)));
            double gw = szX / gn, gh = szY / gn;
            int GX(double x) => Math.Min(gn - 1, Math.Max(0, (int)Math.Floor(x / gw)));
            int GY(double y) => Math.Min(gn - 1, Math.Max(0, (int)Math.Floor(y / gh)));
            var grid = new List<int>[gn * gn];
            for (int k = 0; k < t2.Count; k++)
            {
                var bb = b2[k];
                if (bb.x1 < 0 || bb.x0 > szX || bb.y1 < 0 || bb.y0 > szY) continue;
                for (int gi = GY(bb.y0); gi <= GY(bb.y1); gi++)
                for (int gj = GX(bb.x0); gj <= GX(bb.x1); gj++)
                    (grid[gi * gn + gj] ??= new List<int>()).Add(k);
            }
            var stamp = new int[t2.Count];
            int pass = 0;

            foreach (var a in t1)
            {
                if (!Plane(a, out double a1, out double b1, out double c1)) continue;
                var pa = TriPoly(a);
                pa = ClipLin(pa, 1, 0, 0);          // x ≥ 0
                pa = ClipLin(pa, -1, 0, szX);       // x ≤ szX
                pa = ClipLin(pa, 0, 1, 0);          // y ≥ 0
                pa = ClipLin(pa, 0, -1, szY);       // y ≤ szY
                if (pa.Count < 3) continue;
                Bounds(pa, out double ax0, out double ay0, out double ax1, out double ay1);

                pass++;
                var cand = new List<int>();
                for (int gi = GY(ay0); gi <= GY(ay1); gi++)
                for (int gj = GX(ax0); gj <= GX(ax1); gj++)
                {
                    var l = grid[gi * gn + gj];
                    if (l == null) continue;
                    foreach (int k in l)
                        if (stamp[k] != pass) { stamp[k] = pass; cand.Add(k); }
                }

                foreach (int k in cand)
                {
                    var bb = b2[k];
                    if (bb.x1 < ax0 || bb.x0 > ax1 || bb.y1 < ay0 || bb.y0 > ay1) continue;
                    var b = t2[k];
                    if (!Plane(b, out double a2, out double bq, out double c2)) continue;

                    // Пересечение выпуклых многоугольников: режем по сторонам
                    // треугольника b (против часовой — внутренность слева).
                    var q = pa;
                    var (bx1, by1, bx2, by2, bx3, by3) = Ccw(b);
                    q = ClipEdge(q, bx1, by1, bx2, by2);
                    q = ClipEdge(q, bx2, by2, bx3, by3);
                    q = ClipEdge(q, bx3, by3, bx1, by1);
                    q = Dedupe(q);
                    if (q.Count < 3) continue;
                    double area = Math.Abs(Area(q));
                    if (area < 1e-14) continue;
                    res.Area += area;

                    // h = Z2 − Z1 = ha·x + hb·y + hc
                    double ha = a2 - a1, hb = bq - b1, hc = c2 - c1;
                    double hMax = double.MinValue, hMin = double.MaxValue;
                    foreach (var p in q)
                    {
                        double h = ha * p.X + hb * p.Y + hc;
                        if (h > hMax) hMax = h;
                        if (h < hMin) hMin = h;
                    }
                    if (hMax <= 0 && hMin >= 0) continue;   // h ≡ 0 — работ нет

                    if (hMin >= 0)
                    {
                        fillPieces.Add(q);
                        Integrate(q, ha, hb, hc, ref res.FillArea, ref res.FillVolume);
                        continue;
                    }
                    if (hMax <= 0)
                    {
                        cutPieces.Add(q);
                        Integrate(q, ha, hb, hc, ref res.CutArea, ref res.CutVolume);
                        continue;
                    }

                    var pos = Dedupe(ClipLin(q, ha, hb, hc));
                    var neg = Dedupe(ClipLin(q, -ha, -hb, -hc));
                    if (pos.Count >= 3 && Math.Abs(Area(pos)) > 1e-16)
                    {
                        fillPieces.Add(pos);
                        Integrate(pos, ha, hb, hc, ref res.FillArea, ref res.FillVolume);
                    }
                    if (neg.Count >= 3 && Math.Abs(Area(neg)) > 1e-16)
                    {
                        cutPieces.Add(neg);
                        Integrate(neg, ha, hb, hc, ref res.CutArea, ref res.CutVolume);
                    }

                    // Нулевой отрезок — две точки h = 0 на контуре куска
                    var zs = ZeroChord(q, ha, hb, hc);
                    if (zs.HasValue) zeroSegs.Add(zs.Value);
                }
            }

            var fill = Union(fillPieces);
            var cut  = Union(cutPieces);
            if (fill == null || cut == null) return null;
            res.Fill = fill;
            res.Cut  = cut;
            res.Zero = ChainSegments(zeroSegs);
            return res;
        }

        // ── Геометрия ───────────────────────────────────────────────────────

        private static bool Plane(in Tri t, out double a, out double b, out double c)
        {
            double ux = t.X2 - t.X1, uy = t.Y2 - t.Y1, uz = t.Z2 - t.Z1;
            double vx = t.X3 - t.X1, vy = t.Y3 - t.Y1, vz = t.Z3 - t.Z1;
            double d = ux * vy - uy * vx;
            if (Math.Abs(d) < 1e-14) { a = b = c = 0; return false; }
            a = (uz * vy - uy * vz) / d;
            b = (ux * vz - uz * vx) / d;
            c = t.Z1 - a * t.X1 - b * t.Y1;
            return true;
        }

        private static List<(double X, double Y)> TriPoly(in Tri t)
        {
            var (x1, y1, x2, y2, x3, y3) = Ccw(t);
            return new List<(double X, double Y)> { (x1, y1), (x2, y2), (x3, y3) };
        }

        private static (double, double, double, double, double, double) Ccw(in Tri t)
        {
            double cr = (t.X2 - t.X1) * (t.Y3 - t.Y1) - (t.Y2 - t.Y1) * (t.X3 - t.X1);
            return cr >= 0
                ? (t.X1, t.Y1, t.X2, t.Y2, t.X3, t.Y3)
                : (t.X1, t.Y1, t.X3, t.Y3, t.X2, t.Y2);
        }

        /// <summary>Оставить часть, где a·x + b·y + c ≥ 0.</summary>
        private static List<(double X, double Y)> ClipLin(List<(double X, double Y)> poly,
            double a, double b, double c)
        {
            var res = new List<(double X, double Y)>(poly.Count + 2);
            for (int k = 0; k < poly.Count; k++)
            {
                var p = poly[k]; var q = poly[(k + 1) % poly.Count];
                double fp = a * p.X + b * p.Y + c, fq = a * q.X + b * q.Y + c;
                if (fp >= 0) res.Add(p);
                if ((fp >= 0) != (fq >= 0) && fp != fq)
                {
                    double t = fp / (fp - fq);
                    res.Add((p.X + t * (q.X - p.X), p.Y + t * (q.Y - p.Y)));
                }
            }
            return res;
        }

        /// <summary>Оставить часть слева от направленной прямой (x1,y1)→(x2,y2).</summary>
        private static List<(double X, double Y)> ClipEdge(List<(double X, double Y)> poly,
            double x1, double y1, double x2, double y2)
        {
            double a = -(y2 - y1), b = x2 - x1;
            double c = -(a * x1 + b * y1);
            return ClipLin(poly, a, b, c);
        }

        private static List<(double X, double Y)> Dedupe(List<(double X, double Y)> p)
        {
            var r = new List<(double X, double Y)>(p.Count);
            foreach (var v in p)
                if (r.Count == 0 || !Near(v, r[r.Count - 1])) r.Add(v);
            while (r.Count > 1 && Near(r[0], r[r.Count - 1])) r.RemoveAt(r.Count - 1);
            return r;
        }

        private static bool Near((double X, double Y) a, (double X, double Y) b) =>
            Math.Abs(a.X - b.X) <= Eps && Math.Abs(a.Y - b.Y) <= Eps;

        internal static double Area(List<(double X, double Y)> p)
        {
            double s = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                s += p[j].X * p[i].Y - p[i].X * p[j].Y;
            return s * 0.5;
        }

        private static void Bounds(List<(double X, double Y)> p,
            out double x0, out double y0, out double x1, out double y1)
        {
            x0 = y0 = double.MaxValue; x1 = y1 = double.MinValue;
            foreach (var v in p)
            {
                if (v.X < x0) x0 = v.X; if (v.X > x1) x1 = v.X;
                if (v.Y < y0) y0 = v.Y; if (v.Y > y1) y1 = v.Y;
            }
        }

        /// <summary>Площадь и интеграл линейной h по выпуклому многоугольнику
        /// (веер треугольников: площадь × среднее h в вершинах — точно).</summary>
        private static void Integrate(List<(double X, double Y)> p,
            double ha, double hb, double hc, ref double area, ref double vol)
        {
            var o = p[0];
            double ho = ha * o.X + hb * o.Y + hc;
            for (int k = 1; k + 1 < p.Count; k++)
            {
                var u = p[k]; var w = p[k + 1];
                double s = 0.5 * Math.Abs((u.X - o.X) * (w.Y - o.Y) - (u.Y - o.Y) * (w.X - o.X));
                area += s;
                vol  += s * (ho + ha * u.X + hb * u.Y + hc + ha * w.X + hb * w.Y + hc) / 3.0;
            }
        }

        private static ((double X, double Y), (double X, double Y))? ZeroChord(
            List<(double X, double Y)> q, double ha, double hb, double hc)
        {
            var pts = new List<(double X, double Y)>(2);
            for (int k = 0; k < q.Count; k++)
            {
                var p = q[k]; var r = q[(k + 1) % q.Count];
                double fp = ha * p.X + hb * p.Y + hc, fr = ha * r.X + hb * r.Y + hc;
                if (fp == 0) { pts.Add(p); continue; }
                if ((fp > 0) != (fr > 0) && fr != 0)
                {
                    double t = fp / (fp - fr);
                    pts.Add((p.X + t * (r.X - p.X), p.Y + t * (r.Y - p.Y)));
                }
            }
            pts = Dedupe(pts);
            if (pts.Count < 2) return null;
            // Выпуклый кусок: прямая h = 0 пересекает контур в двух точках —
            // берём самые далёкие (на случай вершин ровно на нуле).
            double best = -1; (double X, double Y) A = pts[0], B = pts[1];
            for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                double d = dx * dx + dy * dy;
                if (d > best) { best = d; A = pts[i]; B = pts[j]; }
            }
            if (best <= Eps * Eps) return null;
            return (A, B);
        }

        // ── Склейка ─────────────────────────────────────────────────────────

        /// <summary>Пул вершин: совпадающие в пределах Eps точки — одна вершина.</summary>
        private sealed class Pool
        {
            private const double Cell = 1e-6;
            private readonly Dictionary<(long, long), List<int>> _grid = new();
            public readonly List<(double X, double Y)> Pts = new();

            public int Get((double X, double Y) p)
            {
                long gx = (long)Math.Floor(p.X / Cell), gy = (long)Math.Floor(p.Y / Cell);
                for (long i = gx - 1; i <= gx + 1; i++)
                for (long j = gy - 1; j <= gy + 1; j++)
                    if (_grid.TryGetValue((i, j), out var l))
                        foreach (int id in l)
                            if (Near(Pts[id], p)) return id;
                int nid = Pts.Count;
                Pts.Add(p);
                if (!_grid.TryGetValue((gx, gy), out var lst)) _grid[(gx, gy)] = lst = new List<int>();
                lst.Add(nid);
                return nid;
            }
        }

        /// <summary>
        /// Объединение кусков (не перекрываются, внутренность слева):
        /// общие рёбра соседних кусков взаимно гасятся, остаток — граница.
        /// Рёбра заранее режутся в вершинах, лежащих на них (Т-стыки).
        /// null — граница не замкнулась.
        /// </summary>
        private static List<List<(double X, double Y)>>? Union(
            List<List<(double X, double Y)>> pieces)
        {
            var res = new List<List<(double X, double Y)>>();
            if (pieces.Count == 0) return res;

            var pool = new Pool();
            var polys = new List<List<int>>(pieces.Count);
            foreach (var pc in pieces)
            {
                var ids = new List<int>(pc.Count);
                foreach (var p in pc)
                {
                    int id = pool.Get(p);
                    if (ids.Count == 0 || ids[ids.Count - 1] != id) ids.Add(id);
                }
                while (ids.Count > 1 && ids[0] == ids[ids.Count - 1]) ids.RemoveAt(ids.Count - 1);
                if (ids.Count >= 3) polys.Add(ids);
            }

            // Индекс вершин для поиска Т-стыков
            var pts = pool.Pts;
            double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < bx0) bx0 = p.X; if (p.X > bx1) bx1 = p.X;
                if (p.Y < by0) by0 = p.Y; if (p.Y > by1) by1 = p.Y;
            }
            int gn = Math.Max(1, Math.Min(256, (int)Math.Sqrt(pts.Count)));
            double gw = Math.Max((bx1 - bx0) / gn, 1e-12), gh = Math.Max((by1 - by0) / gn, 1e-12);
            var grid = new List<int>[gn * gn];
            int GX(double x) => Math.Min(gn - 1, Math.Max(0, (int)((x - bx0) / gw)));
            int GY(double y) => Math.Min(gn - 1, Math.Max(0, (int)((y - by0) / gh)));
            for (int id = 0; id < pts.Count; id++)
            {
                int k = GY(pts[id].Y) * gn + GX(pts[id].X);
                (grid[k] ??= new List<int>()).Add(id);
            }

            // Направленные рёбра с кратностью
            var edges = new Dictionary<(int, int), int>();
            void AddEdge(int u, int v)
            {
                if (u == v) return;
                if (edges.TryGetValue((v, u), out int c))
                {
                    if (c == 1) edges.Remove((v, u)); else edges[(v, u)] = c - 1;
                    return;
                }
                edges[(u, v)] = edges.TryGetValue((u, v), out int c2) ? c2 + 1 : 1;
            }

            var onEdge = new List<(double T, int Id)>();
            foreach (var ids in polys)
            for (int k = 0; k < ids.Count; k++)
            {
                int u = ids[k], v = ids[(k + 1) % ids.Count];
                var p = pts[u]; var q = pts[v];
                double ex = q.X - p.X, ey = q.Y - p.Y, len2 = ex * ex + ey * ey;
                onEdge.Clear();
                int i0 = GY(Math.Min(p.Y, q.Y) - Eps), i1 = GY(Math.Max(p.Y, q.Y) + Eps);
                int j0 = GX(Math.Min(p.X, q.X) - Eps), j1 = GX(Math.Max(p.X, q.X) + Eps);
                for (int gi = i0; gi <= i1; gi++)
                for (int gj = j0; gj <= j1; gj++)
                {
                    var l = grid[gi * gn + gj];
                    if (l == null) continue;
                    foreach (int w in l)
                    {
                        if (w == u || w == v) continue;
                        var r = pts[w];
                        double t = ((r.X - p.X) * ex + (r.Y - p.Y) * ey) / len2;
                        if (t <= 0 || t >= 1) continue;
                        double cx = p.X + t * ex - r.X, cy = p.Y + t * ey - r.Y;
                        if (cx * cx + cy * cy <= Eps * Eps) onEdge.Add((t, w));
                    }
                }
                if (onEdge.Count == 0) { AddEdge(u, v); continue; }
                onEdge.Sort((x, y) => x.T.CompareTo(y.T));
                int prev = u;
                foreach (var (_, w) in onEdge) { AddEdge(prev, w); prev = w; }
                AddEdge(prev, v);
            }

            // Сшивка оставшихся рёбер в контуры
            var outs = new Dictionary<int, List<int>>();
            foreach (var kv in edges)
                for (int c = 0; c < kv.Value; c++)
                {
                    if (!outs.TryGetValue(kv.Key.Item1, out var l)) outs[kv.Key.Item1] = l = new List<int>();
                    l.Add(kv.Key.Item2);
                }

            int remaining = 0;
            foreach (var l in outs.Values) remaining += l.Count;

            while (remaining > 0)
            {
                int start = -1;
                foreach (var kv in outs) if (kv.Value.Count > 0) { start = kv.Key; break; }
                var loop = new List<int> { start };
                int prev = -1, cur = start;
                int guard = remaining + 2;
                while (guard-- > 0)
                {
                    if (!outs.TryGetValue(cur, out var cand) || cand.Count == 0) return null;
                    int pick = 0;
                    if (cand.Count > 1 && prev >= 0)
                    {
                        // Несколько выходов (контуры касаются в точке): берём
                        // самый правый поворот — контуры не перекрещиваются.
                        double inA = Math.Atan2(pts[cur].Y - pts[prev].Y, pts[cur].X - pts[prev].X);
                        double best = double.MaxValue;
                        for (int k = 0; k < cand.Count; k++)
                        {
                            double outA = Math.Atan2(pts[cand[k]].Y - pts[cur].Y, pts[cand[k]].X - pts[cur].X);
                            double turn = outA - inA;
                            while (turn <= -Math.PI) turn += 2 * Math.PI;
                            while (turn > Math.PI) turn -= 2 * Math.PI;
                            if (turn < best) { best = turn; pick = k; }
                        }
                    }
                    int nxt = cand[pick];
                    cand.RemoveAt(pick);
                    remaining--;
                    if (nxt == start) break;
                    loop.Add(nxt);
                    prev = cur; cur = nxt;
                }
                if (guard <= 0) return null;

                var poly = new List<(double X, double Y)>(loop.Count);
                foreach (int id in loop) poly.Add(pts[id]);
                poly = DropCollinear(poly);
                if (poly.Count >= 3 && Math.Abs(Area(poly)) > 1e-12) res.Add(poly);
            }
            return res;
        }

        /// <summary>Убрать вершины на прямой между соседями (в пределах Eps).</summary>
        private static List<(double X, double Y)> DropCollinear(List<(double X, double Y)> a)
        {
            bool changed = true;
            while (changed && a.Count > 3)
            {
                changed = false;
                for (int i = 0; i < a.Count && a.Count > 3; i++)
                {
                    var p0 = a[(i - 1 + a.Count) % a.Count];
                    var p1 = a[i];
                    var p2 = a[(i + 1) % a.Count];
                    double dx = p2.X - p0.X, dy = p2.Y - p0.Y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    double d = len < 1e-15
                        ? Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y))
                        : Math.Abs((p1.X - p0.X) * dy - (p1.Y - p0.Y) * dx) / len;
                    if (d <= Eps) { a.RemoveAt(i); i--; changed = true; }
                }
            }
            return a;
        }

        /// <summary>Отрезки нулевой линии — в ломаные (по совпадающим концам).</summary>
        private static List<List<(double X, double Y)>> ChainSegments(
            List<((double X, double Y) A, (double X, double Y) B)> segs)
        {
            var res = new List<List<(double X, double Y)>>();
            if (segs.Count == 0) return res;

            var pool = new Pool();
            var adj = new Dictionary<int, List<int>>();
            var seen = new HashSet<(int, int)>();
            foreach (var (a, b) in segs)
            {
                int u = pool.Get(a), v = pool.Get(b);
                if (u == v || !seen.Add(u < v ? (u, v) : (v, u))) continue;
                if (!adj.TryGetValue(u, out var lu)) adj[u] = lu = new List<int>();
                if (!adj.TryGetValue(v, out var lv)) adj[v] = lv = new List<int>();
                lu.Add(v); lv.Add(u);
            }

            var used = new HashSet<(int, int)>();
            bool Free(int a, int b) => !used.Contains(a < b ? (a, b) : (b, a));
            foreach (int pass in new[] { 1, 2 })
            foreach (var start in adj.Keys)
            {
                if (pass == 1 && adj[start].Count == 2) continue;
                while (true)
                {
                    int nxt = -1;
                    foreach (int nb in adj[start]) if (Free(start, nb)) { nxt = nb; break; }
                    if (nxt < 0) break;
                    var chain = new List<(double X, double Y)> { pool.Pts[start] };
                    int cur = start;
                    while (nxt >= 0)
                    {
                        used.Add(cur < nxt ? (cur, nxt) : (nxt, cur));
                        chain.Add(pool.Pts[nxt]);
                        cur = nxt; nxt = -1;
                        foreach (int nb in adj[cur]) if (Free(cur, nb)) { nxt = nb; break; }
                    }
                    res.Add(DropCollinearOpen(chain));
                }
            }
            return res;
        }

        private static List<(double X, double Y)> DropCollinearOpen(List<(double X, double Y)> pts)
        {
            if (pts.Count < 3) return pts;
            bool closed = Near(pts[0], pts[pts.Count - 1]);
            var r = new List<(double X, double Y)> { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var a = r[r.Count - 1]; var b = pts[i]; var c = pts[i + 1];
                double dx = c.X - a.X, dy = c.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-15) { r.Add(b); continue; }
                if (Math.Abs((b.X - a.X) * dy - (b.Y - a.Y) * dx) / len > Eps) r.Add(b);
            }
            r.Add(closed ? r[0] : pts[pts.Count - 1]);
            return r;
        }
    }
}
