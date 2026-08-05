using System;
using System.Collections.Generic;
using KartogrammaPlugin;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Обрезка ячейки по «автоматической границе» — краю зоны данных
    //  (KartogrammaPlugin.CellClipper). Режим «Границы автоматически»
    //  со снятым флагом «Не обрезать сетку квадратов».
    //
    //  Проверяем, что контур обрезки повторяет заданную зону данных:
    //  площадь контура сходится с точной площадью зоны, контур замкнут
    //  и лежит внутри ячейки. Ячейка целиком в данных → Full (рисуется
    //  обычный квадрат), совсем без данных → Empty.
    // ═══════════════════════════════════════════════════════════════
    public class CellClipTests
    {
        private const int Steps   = 24;   // плотность выборки
        private const int Refine  = 12;   // итераций бисекции на краю

        private static CellClipper.CellFill Clip(
            double szX, double szY, Func<double, double, bool> hasData,
            out List<List<(double X, double Y)>> loops)
            => CellClipper.Clip(szX, szY, Steps, Refine, hasData, out loops);

        private static double TotalArea(List<List<(double X, double Y)>> loops)
        {
            double s = 0;
            foreach (var l in loops) s += Math.Abs(CellClipper.Area(l));
            return s;
        }

        // ── Вырожденные случаи ────────────────────────────────────────

        [Fact]
        public void Data_Everywhere_IsFull()
        {
            var fill = Clip(5, 5, (x, y) => true, out var loops);
            Assert.Equal(CellClipper.CellFill.Full, fill);
            Assert.Empty(loops);
        }

        [Fact]
        public void No_Data_IsEmpty()
        {
            var fill = Clip(5, 5, (x, y) => false, out var loops);
            Assert.Equal(CellClipper.CellFill.Empty, fill);
            Assert.Empty(loops);
        }

        // ── Прямой рез ────────────────────────────────────────────────

        [Fact]
        public void VerticalCut_KeepsLeftPart_AreaMatches()
        {
            // Данные только левее x = 3.7 в ячейке 10×10 → площадь 37
            var fill = Clip(10, 10, (x, y) => x <= 3.7, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            var loop = Assert.Single(loops);
            Assert.Equal(4, loop.Count);                  // прямоугольник
            Assert.Equal(37.0, TotalArea(loops), 3);
        }

        [Fact]
        public void HorizontalCut_KeepsTopPart_AreaMatches()
        {
            // Данные только выше y = 2.25 в ячейке 5×5 → площадь 5 × 2.75
            var fill = Clip(5, 5, (x, y) => y >= 2.25, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            var loop = Assert.Single(loops);
            Assert.Equal(4, loop.Count);
            Assert.Equal(5 * 2.75, TotalArea(loops), 3);
        }

        // ── Косой рез (типичный край TIN-поверхности) ─────────────────

        [Fact]
        public void DiagonalCut_HalfCell_AreaMatches()
        {
            // Данные ниже диагонали y = x в квадрате 4×4 → ровно половина
            var fill = Clip(4, 4, (x, y) => y <= x, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            var loop = Assert.Single(loops);
            Assert.Equal(3, loop.Count);                  // треугольник
            Assert.Equal(8.0, TotalArea(loops), 2);
        }

        [Fact]
        public void CornerCut_SmallTriangle_AreaMatches()
        {
            // Срезан левый нижний угол: данных нет при x + y < 2 (ячейка 6×6)
            var fill = Clip(6, 6, (x, y) => x + y >= 2.0, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            var loop = Assert.Single(loops);
            Assert.Equal(5, loop.Count);                  // квадрат со срезанным углом
            Assert.Equal(36.0 - 2.0, TotalArea(loops), 2); // 36 − треугольник 2×2/2
        }

        // ── Криволинейный край ────────────────────────────────────────

        [Fact]
        public void CircularEdge_AreaCloseToQuarterDisc()
        {
            // Данные внутри круга R=3 с центром в левом нижнем углу ячейки 4×4:
            // внутрь ячейки попадает четверть круга, π·9/4 ≈ 7.0686
            double R = 3.0;
            var fill = Clip(4, 4, (x, y) => x * x + y * y <= R * R, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            Assert.Single(loops);
            Assert.Equal(Math.PI * R * R / 4.0, TotalArea(loops), 1);
        }

        // ── Несколько несвязных кусков данных в одной ячейке ──────────

        [Fact]
        public void TwoSeparateStrips_ProduceTwoLoops()
        {
            // Две горизонтальные полосы данных, разделённые пустой серединой
            var fill = Clip(10, 10, (x, y) => y <= 2.0 || y >= 8.0, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            Assert.Equal(2, loops.Count);
            Assert.Equal(10 * 2.0 + 10 * 2.0, TotalArea(loops), 2);
        }

        // ── Общие инварианты контура ──────────────────────────────────

        [Fact]
        public void Loops_StayInsideCell_AndAreNotDegenerate()
        {
            const double szX = 7.0, szY = 3.0;
            // Наклонная полоса данных поперёк ячейки
            var fill = Clip(szX, szY, (x, y) => Math.Abs(y - 0.35 * x) <= 1.0, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            foreach (var loop in loops)
            {
                Assert.True(loop.Count >= 3, "контур должен иметь минимум 3 вершины");
                foreach (var p in loop)
                {
                    Assert.InRange(p.X, -1e-9, szX + 1e-9);
                    Assert.InRange(p.Y, -1e-9, szY + 1e-9);
                }
                // Соседние вершины не совпадают
                for (int i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    Assert.True(Math.Abs(a.X - b.X) > 1e-9 || Math.Abs(a.Y - b.Y) > 1e-9,
                        "в контуре не должно быть совпадающих соседних вершин");
                }
            }
        }

        [Fact]
        public void RectangularCells_AreHandled_NonSquareSizes()
        {
            // Неквадратная ячейка 8×2, данные правее x = 5 → площадь 3 × 2
            var fill = Clip(8, 2, (x, y) => x >= 5.0, out var loops);

            Assert.Equal(CellClipper.CellFill.Partial, fill);
            Assert.Equal(3.0 * 2.0, TotalArea(loops), 3);
        }

        // ── Устойчивость: рваная зона не должна ронять построение ─────

        [Fact]
        public void NoisyRegion_DoesNotThrow_AndReturnsClosedLoops()
        {
            var rnd = new Random(20260805);
            var noise = new bool[64, 64];
            for (int i = 0; i < 64; i++)
                for (int j = 0; j < 64; j++)
                    noise[i, j] = rnd.NextDouble() < 0.6;

            bool HasData(double x, double y)
            {
                int j = Math.Clamp((int)(x / 10.0 * 63), 0, 63);
                int i = Math.Clamp((int)(y / 10.0 * 63), 0, 63);
                return noise[i, j];
            }

            var fill = Clip(10, 10, HasData, out var loops);

            // Результат может быть любым (Full как безопасный откат тоже валиден),
            // важно — не падать и не выдавать вырожденные контуры.
            Assert.True(fill == CellClipper.CellFill.Partial ||
                        fill == CellClipper.CellFill.Full);
            foreach (var loop in loops) Assert.True(loop.Count >= 3);
        }
    }
}
