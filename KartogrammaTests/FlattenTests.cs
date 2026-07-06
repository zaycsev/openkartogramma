using System;
using System.Collections.Generic;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Адаптивная аппроксимация криволинейных границ
    //  (KartogrammaProcessor.FlattenClosedCurve / DistPointToSegment).
    //
    //  Регрессия: скруглённый край котлована, нарисованный характерной
    //  линией, обрезался хордой между двумя PI-точками — «круглота»
    //  срезалась. Фикс: между опорными вершинами рекурсивно вставляются
    //  точки, пока стрелка прогиба хорды не станет ≤ 1 см; прямые
    //  участки не разбиваются вовсе.
    // ═══════════════════════════════════════════════════════════════
    public class FlattenTests
    {
        private const double SagTol   = 0.01;
        private const int    MaxDepth = 10;

        // Копия KartogrammaProcessor.DistPointToSegment.
        private static double DistPointToSegment(Pt2d p, Pt2d a, Pt2d b)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double len2 = abx * abx + aby * aby;
            if (len2 < 1e-18)
            {
                double dx0 = p.X - a.X, dy0 = p.Y - a.Y;
                return Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            }
            double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = a.X + t * abx, qy = a.Y + t * aby;
            double dx = p.X - qx, dy = p.Y - qy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Копия рекурсии Subdivide из FlattenClosedCurve: кривая задана
        // делегатом «дистанция вдоль кривой → точка» (аналог GetPointAtDist).
        private static void Subdivide(Func<double, Pt2d> curve, List<Pt2d> outPts,
            double dA, Pt2d pA, double dB, Pt2d pB, int depth)
        {
            if (depth >= MaxDepth || dB - dA <= 1e-6) return;
            // Вырожденная хорда (pA≈pB): стрелку прогиба не измерить,
            // деление ушло бы в полный обход контура — не делим.
            double ddx = pA.X - pB.X, ddy = pA.Y - pB.Y;
            if (Math.Sqrt(ddx * ddx + ddy * ddy) < 1e-6) return;
            double dM = (dA + dB) / 2.0;
            var pM = curve(dM);
            if (DistPointToSegment(pM, pA, pB) <= SagTol) return;
            Subdivide(curve, outPts, dA, pA, dM, pM, depth + 1);
            outPts.Add(pM);
            Subdivide(curve, outPts, dM, pM, dB, pB, depth + 1);
        }

        private static List<Pt2d> Flatten(Func<double, Pt2d> curve,
            double dA, double dB)
        {
            var pts = new List<Pt2d> { curve(dA) };
            Subdivide(curve, pts, dA, curve(dA), dB, curve(dB), 0);
            pts.Add(curve(dB));
            return pts;
        }

        // ── Дуга (скругление котлована) ──────────────────────────────────

        [Fact]
        public void QuarterArc_R5_SubdividedWithinTolerance()
        {
            // Четверть окружности R=5 м — как скругление угла котлована.
            // Кривая параметризована длиной дуги: d ∈ [0 .. πR/2]
            const double R = 5.0;
            Pt2d Arc(double d) => new(R * Math.Cos(d / R), R * Math.Sin(d / R));

            var pts = Flatten(Arc, 0, Math.PI * R / 2);

            // Раньше была одна хорда со стрелкой ≈ R(1−cos45°) ≈ 1.46 м(!).
            // Теперь каждая хорда отклоняется от дуги не более чем на 1 см.
            Assert.True(pts.Count >= 8);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                // Стрелка прогиба хорды = R − расстояние от центра до её середины
                double mx = (pts[i].X + pts[i + 1].X) / 2;
                double my = (pts[i].Y + pts[i + 1].Y) / 2;
                double sag = R - Math.Sqrt(mx * mx + my * my);
                Assert.True(sag <= SagTol + 1e-9,
                    $"хорда {i}: стрелка {sag:F4} м > допуска {SagTol} м");
            }
        }

        [Fact]
        public void StraightSegment_NotSubdivided()
        {
            // Прямой участок: середина лежит на хорде → ни одной лишней точки
            Pt2d Line(double d) => new(d, 2 * d);
            var pts = Flatten(Line, 0, 50);
            Assert.Equal(2, pts.Count);
        }

        [Fact]
        public void DegenerateChord_SamePointOverFullLoop_NotSubdivided()
        {
            // Регрессия «сетка не строится»: у контура, замкнутого совпадением
            // концов, замыкающая пара — две ОДИНАКОВЫЕ точки, а «сегмент» между
            // ними — весь контур. Деление такой пары обвело бы контур второй раз
            // (полигон стал бы самоналегающим и классификация ячеек ломалась).
            const double R = 10.0;
            Pt2d Circle(double d) => new(R * Math.Cos(d / R), R * Math.Sin(d / R));

            var outPts = new List<Pt2d>();
            var p0 = Circle(0);
            Subdivide(Circle, outPts, 0, p0, 2 * Math.PI * R, p0, 0);

            Assert.Empty(outPts);   // вырожденная хорда не делится вовсе
        }

        [Fact]
        public void TinyArc_DepthLimited_NoInfiniteRecursion()
        {
            // Микродуга радиуса 3 мм: допуск недостижим лучше глубины —
            // рекурсия обязана остановиться на MaxDepth
            const double R = 0.003;
            Pt2d Arc(double d) => new(R * Math.Cos(d / R), R * Math.Sin(d / R));
            var pts = Flatten(Arc, 0, Math.PI * R / 2);
            Assert.True(pts.Count >= 2);           // не зациклилось и не упало
        }

        // ── DistPointToSegment ───────────────────────────────────────────

        [Fact]
        public void DistPointToSegment_PerpendicularFoot()
        {
            Assert.Equal(3.0, DistPointToSegment(new Pt2d(5, 3), new Pt2d(0, 0), new Pt2d(10, 0)), 9);
        }

        [Fact]
        public void DistPointToSegment_BeyondEnd_ClampsToEndpoint()
        {
            Assert.Equal(5.0, DistPointToSegment(new Pt2d(13, 4), new Pt2d(0, 0), new Pt2d(10, 0)), 9);
        }

        [Fact]
        public void DistPointToSegment_DegenerateSegment()
        {
            Assert.Equal(5.0, DistPointToSegment(new Pt2d(3, 4), new Pt2d(0, 0), new Pt2d(0, 0)), 9);
        }
    }
}
