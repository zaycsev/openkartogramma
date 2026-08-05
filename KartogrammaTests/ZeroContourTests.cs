using System;
using System.Collections.Generic;
using KartogrammaPlugin;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Трассировка линии нулевых работ (KartogrammaPlugin.ZeroContour).
    //
    //  Линия должна проходить ровно там, где рабочая отметка обращается
    //  в ноль: по ней делятся объёмы (ZeroLineSplit) и обрезаются
    //  штриховки, поэтому расхождение картинки и цифр недопустимо.
    // ═══════════════════════════════════════════════════════════════
    public class ZeroContourTests
    {
        private const int Steps = 20;

        private static List<List<(double X, double Y)>> Trace(
            double szX, double szY, Func<double, double, double> h)
            => ZeroContour.Trace(szX, szY, Steps, h);

        // ── Нечего трассировать ───────────────────────────────────────

        [Fact]
        public void AllFill_NoContour()
        {
            var res = Trace(10, 10, (x, y) => 2.0);
            Assert.Empty(res);
        }

        [Fact]
        public void AllCut_NoContour()
        {
            var res = Trace(10, 10, (x, y) => -2.0);
            Assert.Empty(res);
        }

        [Fact]
        public void NoData_NoContour()
        {
            var res = Trace(10, 10, (x, y) => double.NaN);
            Assert.Empty(res);
        }

        // ── Прямая нулевая линия ──────────────────────────────────────

        [Fact]
        public void VerticalZeroLine_AtExactPosition()
        {
            // h меняет знак при x = 3.5 в ячейке 10×10
            var res = Trace(10, 10, (x, y) => x - 3.5);

            var chain = Assert.Single(res);
            // Прямая линия после упрощения — две вершины
            Assert.Equal(2, chain.Count);
            foreach (var p in chain)
                Assert.Equal(3.5, p.X, 6);
            // Тянется через всю ячейку по Y
            Assert.Equal(0.0, Math.Min(chain[0].Y, chain[1].Y), 6);
            Assert.Equal(10.0, Math.Max(chain[0].Y, chain[1].Y), 6);
        }

        [Fact]
        public void HorizontalZeroLine_AtExactPosition()
        {
            var res = Trace(8, 6, (x, y) => y - 2.25);

            var chain = Assert.Single(res);
            Assert.Equal(2, chain.Count);
            foreach (var p in chain)
                Assert.Equal(2.25, p.Y, 6);
        }

        [Fact]
        public void DiagonalZeroLine_LiesOnTheDiagonal()
        {
            // h = y − x → нулевая линия это диагональ y = x
            var res = Trace(10, 10, (x, y) => y - x);

            var chain = Assert.Single(res);
            Assert.True(chain.Count >= 2);
            foreach (var p in chain)
                Assert.Equal(p.X, p.Y, 6);
        }

        // ── Замкнутый контур внутри ячейки ────────────────────────────

        [Fact]
        public void IslandOfFill_GivesClosedLoop()
        {
            // Холмик посреди выемки: h > 0 внутри круга R=2 в центре ячейки 10×10
            var res = Trace(10, 10, (x, y) =>
            {
                double dx = x - 5, dy = y - 5;
                return 4.0 - (dx * dx + dy * dy);   // >0 внутри R=2
            });

            var chain = Assert.Single(res);
            Assert.True(chain.Count >= 8, "окружность должна дать многоугольник");

            // Все точки на расстоянии R=2 от центра
            foreach (var p in chain)
            {
                double d = Math.Sqrt((p.X - 5) * (p.X - 5) + (p.Y - 5) * (p.Y - 5));
                Assert.InRange(d, 1.9, 2.1);
            }

            // Контур замкнут: первая и последняя точки совпадают
            Assert.Equal(chain[0].X, chain[chain.Count - 1].X, 6);
            Assert.Equal(chain[0].Y, chain[chain.Count - 1].Y, 6);
        }

        // ── Две отдельные линии ───────────────────────────────────────

        [Fact]
        public void TwoZeroLines_GiveTwoChains()
        {
            // Полоса насыпи между двумя выемками: два пересечения нуля
            var res = Trace(12, 4, (x, y) => (x > 4 && x < 8) ? 1.0 : -1.0);

            Assert.Equal(2, res.Count);
        }

        // ── Обрыв на краю данных ──────────────────────────────────────

        [Fact]
        public void ContourStopsWhereDataEnds()
        {
            // Данные только в левой половине ячейки; нулевая линия вертикальная
            var res = Trace(10, 10, (x, y) =>
            {
                if (x > 5.0) return double.NaN;      // нет данных справа
                return y - 5.0;
            });

            Assert.NotEmpty(res);
            foreach (var chain in res)
                foreach (var p in chain)
                    Assert.True(p.X <= 5.0 + 1e-9,
                        "линия не должна заходить в область без данных");
        }

        // ── Общие инварианты ──────────────────────────────────────────

        [Fact]
        public void AllPointsStayInsideCell()
        {
            const double szX = 7.0, szY = 3.0;
            var res = Trace(szX, szY, (x, y) => Math.Sin(x) + Math.Cos(y) - 0.3);

            Assert.NotEmpty(res);
            foreach (var chain in res)
            {
                Assert.True(chain.Count >= 2);
                foreach (var p in chain)
                {
                    Assert.InRange(p.X, -1e-9, szX + 1e-9);
                    Assert.InRange(p.Y, -1e-9, szY + 1e-9);
                }
            }
        }

        [Fact]
        public void ZeroLineAgreesWithVolumeSplit()
        {
            // Ключевая связка: там, где линия делит ячейку пополам,
            // разделение объёма тоже должно дать половину на половину.
            var res = Trace(10, 10, (x, y) => x - 5.0);
            var chain = Assert.Single(res);
            foreach (var p in chain) Assert.Equal(5.0, p.X, 6);

            // Треугольник с отметками (−5, +5, +5) — линия отсекает четверть
            ZeroLineSplit.Triangle(100.0, -5.0, 5.0, 5.0,
                out double f, out double c, out double af, out double ac);
            Assert.Equal(25.0, ac, 6);
            Assert.Equal(75.0, af, 6);
            Assert.Equal(100.0 * (-5 + 5 + 5) / 3.0, f + c, 6);
        }
    }
}
