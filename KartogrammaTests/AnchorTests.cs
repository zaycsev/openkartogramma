using System;
using System.Collections.Generic;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Якорь подписи объёма ячейки (KartogrammaProcessor.FindInsideAnchor).
    //
    //  Регрессия (траншея-рамка, крупные ячейки): цифры объёмов рисовались
    //  за пределами заданных внешней/внутренних границ. Причины:
    //    1) старый код проверял IsInBounds, который в режиме «Не обрезать»
    //       (DontClipCells=true) возвращает true для ЛЮБОЙ точки — центр
    //       полной ячейки оставался в «дырке» или за наружной границей;
    //    2) запасная выборка 7×7 (шаг ~1.4 м при ячейке 10 м) промахивалась
    //       мимо узкой полосы траншеи (0.8 м) — fallback возвращал центр;
    //    3) «ближайшая к центру» точка сажала текст на самый край полосы.
    //
    //  Фикс: проверка чисто геометрическая (IsInClipRegion) в обоих режимах,
    //  выборка плотная (DenseOverlapSteps), якорь — центроид внутренних
    //  точек (или ближайшая к нему внутренняя, если центроид вне области).
    // ═══════════════════════════════════════════════════════════════
    public class AnchorTests
    {
        // Клип-область: внутри outer и вне всех inner (копия IsInClipRegion,
        // без режимной логики — фикс использует именно геометрический тест).
        private static bool InClipRegion(Pt2d p, List<Pt2d>? outer, List<List<Pt2d>>? inners)
        {
            if (outer == null && inners == null) return true;
            if (outer != null && !Geo.PointInPolygon(p, outer)) return false;
            if (inners != null)
                foreach (var ip in inners)
                    if (Geo.PointInPolygon(p, ip)) return false;
            return true;
        }

        // Копия KartogrammaProcessor.DenseOverlapSteps.
        private static int DenseOverlapSteps(double cellSize, double volumeNodeStep)
        {
            int n = (int)Math.Ceiling(cellSize / Math.Max(volumeNodeStep, 1e-6));
            return Math.Clamp(n, 4, 40);
        }

        // Копия логики KartogrammaProcessor.FindInsideAnchor (без поворота:
        // cA=1, sA=0, BaseX=BaseY=0 — локальные координаты = мировым).
        private static void FindInsideAnchor(
            double cellX0, double cellY0, double szX, double szY,
            double ancX, double ancY,
            List<Pt2d>? outer, List<List<Pt2d>>? inners,
            double volumeNodeStep,
            out double outX, out double outY)
        {
            outX = ancX; outY = ancY;
            if (outer == null && inners == null) return;
            if (InClipRegion(new Pt2d(ancX, ancY), outer, inners)) return;

            int n = Math.Max(7, DenseOverlapSteps(Math.Max(szX, szY), volumeNodeStep));
            var inPts = new List<Pt2d>(n * n);
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var p = new Pt2d(cellX0 + szX * (j + 0.5) / n,
                                 cellY0 + szY * (i + 0.5) / n);
                if (InClipRegion(p, outer, inners)) inPts.Add(p);
            }
            if (inPts.Count == 0) return;

            double cx = 0, cy = 0;
            foreach (var p in inPts) { cx += p.X; cy += p.Y; }
            cx /= inPts.Count; cy /= inPts.Count;

            if (InClipRegion(new Pt2d(cx, cy), outer, inners))
            {
                outX = cx; outY = cy;
                return;
            }

            double best = double.MaxValue;
            foreach (var p in inPts)
            {
                double dx = p.X - cx, dy = p.Y - cy;
                double d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; outX = p.X; outY = p.Y; }
            }
        }

        private static List<Pt2d> Rect(double x0, double y0, double x1, double y1) =>
            new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

        // ── Траншея-рамка: outer 0..30, «дырка» 5..25 → полоса шириной 5 ──

        private static readonly List<Pt2d> RingOuter = Rect(0, 0, 30, 30);
        private static readonly List<List<Pt2d>> RingInner = new() { Rect(5, 5, 25, 25) };

        [Fact]
        public void CellCenterInsideHole_AnchorMovesIntoBand()
        {
            // Ячейка 2..12 × 10..20 задевает левую полосу рамки (x 0..5),
            // но её центр (7,15) — в «дырке»
            FindInsideAnchor(2, 10, 10, 10, 7, 15, RingOuter, RingInner, 0.05,
                out double x, out double y);

            Assert.True(InClipRegion(new Pt2d(x, y), RingOuter, RingInner));
            Assert.InRange(x, 2, 5);            // якорь ушёл в полосу рамки
        }

        [Fact]
        public void CellCenterOutsideOuter_AnchorMovesIntoBand()
        {
            // Ячейка левее рамки, заходит в неё краем: x −8..2, центр (−3,15) снаружи
            FindInsideAnchor(-8, 10, 10, 10, -3, 15, RingOuter, RingInner, 0.05,
                out double x, out double y);

            Assert.True(InClipRegion(new Pt2d(x, y), RingOuter, RingInner));
            // Якорь в левой полосе рамки (0..5), по середине видимого куска
            Assert.InRange(x, 0, 5);
        }

        [Fact]
        public void CornerCell_LShapedPiece_AnchorInsideBand()
        {
            // Угловая ячейка 0..10: видимый кусок L-образный (полосы 0..5 по X и Y),
            // центроид L может попасть в «дырку» — якорь всё равно обязан быть внутри
            FindInsideAnchor(0, 0, 10, 10, 5, 5, RingOuter, RingInner, 0.05,
                out double x, out double y);

            Assert.True(InClipRegion(new Pt2d(x, y), RingOuter, RingInner));
        }

        // ── Узкая полоса 0.8 м в крупной ячейке (промах выборки 7×7) ──────

        [Fact]
        public void ThinBand_CoarseSevenGridMissesIt_DenseFindsIt()
        {
            // Полоса x∈[6.6..7.4] — узлы 7×7 ((j+0.5)·10/7: 0.71, 2.14, …, 9.29) мимо
            var band = Rect(6.6, -100, 7.4, 100);

            FindInsideAnchor(0, 0, 10, 10, 5, 5, band, null, 0.05,
                out double x, out double y);

            Assert.True(InClipRegion(new Pt2d(x, y), band, null));
            Assert.InRange(x, 6.6, 7.4);
            // Центроид внутренних точек — по середине полосы, не на её краю
            Assert.Equal(7.0, x, 1);
        }

        [Fact]
        public void SevenGrid_ReallyMissesThinBand()
        {
            // Документация промаха: все узлы 7×7 вне полосы 6.6..7.4
            for (int j = 0; j < 7; j++)
            {
                double x = 10.0 * (j + 0.5) / 7;
                Assert.False(x >= 6.6 && x <= 7.4);
            }
        }

        // ── Прочее поведение ──────────────────────────────────────────────

        [Fact]
        public void AnchorAlreadyInside_Unchanged()
        {
            FindInsideAnchor(0, 0, 10, 10, 2.5, 2.5, RingOuter, RingInner, 0.05,
                out double x, out double y);
            Assert.Equal(2.5, x, 9);
            Assert.Equal(2.5, y, 9);
        }

        [Fact]
        public void NoBoundaries_AnchorUnchanged()
        {
            FindInsideAnchor(0, 0, 10, 10, 5, 5, null, null, 0.05,
                out double x, out double y);
            Assert.Equal(5.0, x, 9);
            Assert.Equal(5.0, y, 9);
        }

        [Fact]
        public void CellFullyOutside_FallbackToOriginal()
        {
            // Ячейка целиком за рамкой — внутренних точек нет, якорь не трогаем
            // (такие ячейки и так отфильтрованы в BuildCells и объёма не имеют)
            FindInsideAnchor(100, 100, 10, 10, 105, 105, RingOuter, RingInner, 0.05,
                out double x, out double y);
            Assert.Equal(105.0, x, 9);
            Assert.Equal(105.0, y, 9);
        }
    }
}
