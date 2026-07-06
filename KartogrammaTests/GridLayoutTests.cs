using System;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Раскладка сетки квадратов по оси (KartogrammaProcessor.LayoutGridAxis).
    //
    //  Объединение поведения 1.1.1 (котлован) и 1.1.2 (узкая траншея):
    //    • зона у́же одной ячейки → 1 ячейка, зона по центру
    //      (узкая траншея сидит в одном ряду, а не на стыке двух);
    //    • ширина зоны кратна шагу → ровно n ячеек, точная посадка;
    //    • иначе → n целых ячеек в середине + 2 симметричных краевых
    //      обрезка (эстетика 1.1.1: внутренность замощена целыми
    //      квадратами, граница режет только тонкие полосы).
    //
    //  Регрессия 1.1.2: на тестовом котловане ~34×25.7 м с сеткой 10×10
    //  строилось лишь 2 целых квадрата (ceil по зоне) вместо 6 (3×2) в 1.1.1.
    // ═══════════════════════════════════════════════════════════════
    public class GridLayoutTests
    {
        // Копия KartogrammaProcessor.LayoutGridAxis (проект плагина зависит от
        // AutoCAD/Civil DLL и не подключается к тестам напрямую).
        private static void LayoutGridAxis(double zoneMin, double zoneMax, double step,
            out int count, out double gridMin)
        {
            double w = Math.Max(zoneMax - zoneMin, 0.0);
            const double tol = 1e-3;
            int n = (int)Math.Floor(w / step + 1e-9);
            double rem = w - n * step;

            if      (n <= 0)     count = 1;
            else if (rem <= tol) count = n;
            else                 count = n + 2;
            count = Math.Clamp(count, 1, 500);

            gridMin = zoneMin - (count * step - w) / 2.0;
        }

        // Сколько ячеек раскладки лежит ЦЕЛИКОМ внутри зоны.
        private static int WholeCellsInside(double zoneMin, double zoneMax,
            double step, int count, double gridMin)
        {
            int whole = 0;
            for (int i = 0; i < count; i++)
            {
                double a = gridMin + i * step, b = a + step;
                if (a >= zoneMin - 1e-9 && b <= zoneMax + 1e-9) whole++;
            }
            return whole;
        }

        // ── Котлован (регрессия 1.1.2 → эстетика 1.1.1) ─────────────────

        [Fact]
        public void Pit34m_Step10_Gives3WholeCellsPlusTwoStrips()
        {
            LayoutGridAxis(0, 34, 10, out int count, out double gridMin);

            Assert.Equal(5, count);                          // 3 целых + 2 обрезка
            Assert.Equal(-8.0, gridMin, 9);                  // излишек 16 м поровну
            Assert.Equal(3, WholeCellsInside(0, 34, 10, count, gridMin));

            // Краевые обрезки симметричны: по (34 − 30)/2 = 2 м с каждой стороны
            double firstInnerLine = gridMin + 10;            // −8 + 10 = 2
            Assert.Equal(2.0, firstInnerLine, 9);
        }

        [Fact]
        public void Pit25_7m_Step10_Gives2WholeCellsPlusTwoStrips()
        {
            LayoutGridAxis(0, 25.7, 10, out int count, out double gridMin);

            Assert.Equal(4, count);
            Assert.Equal(2, WholeCellsInside(0, 25.7, 10, count, gridMin));

            // Обрезки по (25.7 − 20)/2 = 2.85 м
            Assert.Equal(2.85, gridMin + 10, 9);
        }

        // ── Узкая траншея (поведение 1.1.2 сохраняется) ─────────────────

        [Fact]
        public void NarrowTrench_ZoneThinnerThanCell_SingleCenteredRow()
        {
            LayoutGridAxis(100, 104, 10, out int count, out double gridMin);

            Assert.Equal(1, count);
            // Зона [100..104] по центру ячейки [97..107] — не на стыке двух рядов
            Assert.Equal(97.0, gridMin, 9);
            Assert.True(gridMin <= 100 && gridMin + 10 >= 104);
        }

        [Fact]
        public void TrenchLength_ExactMultiple_FitsExactly()
        {
            LayoutGridAxis(50, 110, 10, out int count, out double gridMin);

            Assert.Equal(6, count);                          // ровно 60 / 10
            Assert.Equal(50.0, gridMin, 9);                  // сетка совпадает с границей
        }

        [Fact]
        public void NearMultiple_WithinMillimeter_TreatedAsExact()
        {
            LayoutGridAxis(0, 60.0005, 10, out int count, out double gridMin);

            Assert.Equal(6, count);                          // без ячеек-«лезвий»
        }

        // ── Общие свойства ───────────────────────────────────────────────

        [Theory]
        [InlineData(0, 34,    10)]
        [InlineData(0, 25.7,  10)]
        [InlineData(0, 17,     6)]
        [InlineData(-12, 43.3, 5)]
        [InlineData(7, 7.4,   10)]
        public void GridAlwaysCoversZone_Symmetrically(double zMin, double zMax, double step)
        {
            LayoutGridAxis(zMin, zMax, step, out int count, out double gridMin);
            double gridMax = gridMin + count * step;

            Assert.True(gridMin <= zMin + 1e-9);
            Assert.True(gridMax >= zMax - 1e-9);
            // Симметрия: свес слева == свесу справа
            Assert.Equal(zMin - gridMin, gridMax - zMax, 9);
        }

        [Fact]
        public void NonMultipleZone_AlwaysYieldsFloorWholeCells()
        {
            // Целых ячеек внутри всегда floor(W/step) — максимум возможного
            for (double w = 3; w < 97; w += 1.7)
            {
                LayoutGridAxis(0, w, 10, out int count, out double gridMin);
                int expected = (int)Math.Floor(w / 10 + 1e-9);
                Assert.Equal(expected == 0 ? 0 : expected,
                    WholeCellsInside(0, w, 10, count, gridMin));
            }
        }

        [Fact]
        public void HugeZone_ClampedTo500Cells()
        {
            LayoutGridAxis(0, 1e6, 10, out int count, out _);
            Assert.Equal(500, count);
        }
    }
}
