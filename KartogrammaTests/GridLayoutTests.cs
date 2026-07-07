using System;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Раскладка сетки квадратов по оси (KartogrammaProcessor.LayoutGridAxis).
    //
    //  Два режима; выбирается АВТОМАТИЧЕСКИ по заданным границам:
    //    внешняя + внутренние — точная посадка; только внешняя или
    //    авто — целые квадраты (см. KartogrammaProcessor.CalcAutoGrid).
    //    • wholeCells=false (по умолчанию) — ТОЧНАЯ посадка исходной 1.1.2:
    //      минимум ячеек ceil(W/шаг), сетка натянута на границу. Эталон
    //      пользователя: траншея 31.798×22.219 с сеткой 10×10 → 4×3 ячейки,
    //      яма под зумпф попадает в свою ячейку (объём −10.06 виден отдельно);
    //    • wholeCells=true — стиль 1.1.1 для котлованов: n целых квадратов
    //      в середине + два симметричных краевых обрезка. Эталон: котлован
    //      ~34×25.7 с сеткой 10×10 → 3×2 = 6 целых квадратов.
    //
    //  Общее: зона у́же ячейки → 1 ячейка по центру; кратная ширина → точная
    //  посадка. Излишек всегда поровну с двух сторон.
    // ═══════════════════════════════════════════════════════════════
    public class GridLayoutTests
    {
        // Копия KartogrammaProcessor.LayoutGridAxis (проект плагина зависит от
        // AutoCAD/Civil DLL и не подключается к тестам напрямую).
        private static void LayoutGridAxis(double zoneMin, double zoneMax, double step,
            bool wholeCells, out int count, out double gridMin)
        {
            double w = Math.Max(zoneMax - zoneMin, 0.0);
            const double tol = 1e-3;
            int n = (int)Math.Floor(w / step + 1e-9);
            double rem = w - n * step;

            if      (n <= 0)     count = 1;
            else if (rem <= tol) count = n;
            else                 count = wholeCells ? n + 2 : n + 1;
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

        // ── Режим «точно по границе» (исходная 1.1.2, по умолчанию) ──────

        [Fact]
        public void Exact_UserTrench31_798_Step10_Gives4Cols()
        {
            // Эталон пользователя: именно эта раскладка давала −3.53 / −10.06 / −3.10
            LayoutGridAxis(0, 31.798, 10, wholeCells: false, out int count, out double gridMin);

            Assert.Equal(4, count);                          // ceil(31.798/10)
            Assert.Equal(-(40 - 31.798) / 2, gridMin, 9);    // излишек поровну
        }

        [Fact]
        public void Exact_UserTrench22_219_Step10_Gives3Rows()
        {
            LayoutGridAxis(0, 22.219, 10, wholeCells: false, out int count, out _);
            Assert.Equal(3, count);                          // ceil(22.219/10)
        }

        [Fact]
        public void Exact_Pit34_Step10_Gives4Cols_MinimumCells()
        {
            LayoutGridAxis(0, 34, 10, wholeCells: false, out int count, out double gridMin);
            Assert.Equal(4, count);
            Assert.Equal(-3.0, gridMin, 9);                  // (40−34)/2 = 3 свеса
        }

        // ── Режим «целые квадраты» (стиль 1.1.1, для котлованов) ─────────

        [Fact]
        public void Whole_Pit34m_Step10_Gives3WholeCellsPlusTwoStrips()
        {
            LayoutGridAxis(0, 34, 10, wholeCells: true, out int count, out double gridMin);

            Assert.Equal(5, count);                          // 3 целых + 2 обрезка
            Assert.Equal(-8.0, gridMin, 9);                  // излишек 16 м поровну
            Assert.Equal(3, WholeCellsInside(0, 34, 10, count, gridMin));

            // Краевые обрезки симметричны: по (34 − 30)/2 = 2 м с каждой стороны
            Assert.Equal(2.0, gridMin + 10, 9);
        }

        [Fact]
        public void Whole_Pit25_7m_Step10_Gives2WholeCellsPlusTwoStrips()
        {
            LayoutGridAxis(0, 25.7, 10, wholeCells: true, out int count, out double gridMin);

            Assert.Equal(4, count);
            Assert.Equal(2, WholeCellsInside(0, 25.7, 10, count, gridMin));
            Assert.Equal(2.85, gridMin + 10, 9);             // обрезки (25.7−20)/2
        }

        [Fact]
        public void Whole_NonMultipleZone_AlwaysYieldsFloorWholeCells()
        {
            for (double w = 3; w < 97; w += 1.7)
            {
                LayoutGridAxis(0, w, 10, wholeCells: true, out int count, out double gridMin);
                int expected = (int)Math.Floor(w / 10 + 1e-9);
                Assert.Equal(expected == 0 ? 0 : expected,
                    WholeCellsInside(0, w, 10, count, gridMin));
            }
        }

        // ── Общее для обоих режимов ───────────────────────────────────────

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NarrowTrench_ZoneThinnerThanCell_SingleCenteredRow(bool whole)
        {
            LayoutGridAxis(100, 104, 10, whole, out int count, out double gridMin);

            Assert.Equal(1, count);
            // Зона [100..104] по центру ячейки [97..107] — не на стыке двух рядов
            Assert.Equal(97.0, gridMin, 9);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExactMultiple_FitsExactly(bool whole)
        {
            LayoutGridAxis(50, 110, 10, whole, out int count, out double gridMin);
            Assert.Equal(6, count);                          // ровно 60 / 10
            Assert.Equal(50.0, gridMin, 9);                  // сетка совпадает с границей
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NearMultiple_WithinMillimeter_TreatedAsExact(bool whole)
        {
            LayoutGridAxis(0, 60.0005, 10, whole, out int count, out _);
            Assert.Equal(6, count);                          // без ячеек-«лезвий»
        }

        [Theory]
        [InlineData(0, 34,    10, false)]
        [InlineData(0, 34,    10, true)]
        [InlineData(0, 25.7,  10, false)]
        [InlineData(0, 25.7,  10, true)]
        [InlineData(-12, 43.3, 5, false)]
        [InlineData(-12, 43.3, 5, true)]
        [InlineData(7, 7.4,   10, false)]
        [InlineData(7, 7.4,   10, true)]
        public void GridAlwaysCoversZone_Symmetrically(double zMin, double zMax,
            double step, bool whole)
        {
            LayoutGridAxis(zMin, zMax, step, whole, out int count, out double gridMin);
            double gridMax = gridMin + count * step;

            Assert.True(gridMin <= zMin + 1e-9);
            Assert.True(gridMax >= zMax - 1e-9);
            // Симметрия: свес слева == свесу справа
            Assert.Equal(zMin - gridMin, gridMax - zMax, 9);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HugeZone_ClampedTo500Cells(bool whole)
        {
            LayoutGridAxis(0, 1e6, 10, whole, out int count, out _);
            Assert.Equal(500, count);
        }
    }
}
