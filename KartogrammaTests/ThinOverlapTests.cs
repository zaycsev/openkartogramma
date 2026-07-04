using System;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Регрессия: тонкая траншея (узкая полоса перекрытия) не должна
    //  теряться при крупной сетке.
    //
    //  Баг: у траншеи-«рамки» шириной 0.8 м обе поверхности перекрываются
    //  только внутри этой узкой полосы. При построении сетки 10×10 м грубая
    //  выборка 3×3 (точки на 0 / 5 / 10 м) проходит МИМО полосы 0.8 м —
    //  ячейка считается «нет перекрытия» и сетка не строится. С ячейкой
    //  0.8 м выборка достаточно плотная и всё работает.
    //
    //  Фикс (KartogrammaProcessor): при ручных границах, если грубая выборка
    //  не нашла перекрытие, ячейка досэмплируется плотнее (DenseOverlapSteps,
    //  шаг ≈ VolumeNodeStep, ограничен сверху).
    // ═══════════════════════════════════════════════════════════════
    public class ThinOverlapTests
    {
        // Перекрытие двух поверхностей существует только в вертикальной полосе
        // шириной 0.8 м (имитация стенки траншеи внутри ячейки 10×10 м).
        // Полоса намеренно смещена так, чтобы не попадать на узлы 0/5/10.
        private static bool BandOverlap(double lx) => lx >= 6.6 && lx <= 7.4;

        // Копия логики KartogrammaProcessor.CellHasOverlap: steps интервалов
        // по стороне ячейки (steps+1 узлов).
        private static bool CellHasOverlap(double cellSize, int steps)
        {
            for (int i = 0; i <= steps; i++)
            for (int j = 0; j <= steps; j++)
            {
                double lx = j * cellSize / steps;
                if (BandOverlap(lx)) return true;
            }
            return false;
        }

        // Копия KartogrammaProcessor.DenseOverlapSteps.
        private static int DenseOverlapSteps(double cellSize, double volumeNodeStep)
        {
            int n = (int)Math.Ceiling(cellSize / Math.Max(volumeNodeStep, 1e-6));
            return Math.Clamp(n, 4, 40);
        }

        [Fact]
        public void CoarseSampling_MissesThinTrench()
        {
            // Грубая 3×3 сетка (steps=2): узлы на 0 / 5 / 10 — все мимо полосы 6.6..7.4
            Assert.False(CellHasOverlap(10.0, steps: 2));
        }

        [Fact]
        public void DenseSampling_DetectsThinTrench()
        {
            int steps = DenseOverlapSteps(10.0, volumeNodeStep: 0.05);
            Assert.True(CellHasOverlap(10.0, steps));
        }

        [Fact]
        public void DenseSteps_SpacingFinerThanTrenchWidth()
        {
            // Шаг плотной выборки должен быть меньше ширины траншеи (0.8 м),
            // иначе полоса снова может проскочить между узлами.
            int steps = DenseOverlapSteps(10.0, volumeNodeStep: 0.05);
            double spacing = 10.0 / steps;
            Assert.True(spacing < 0.8,
                $"шаг плотной выборки {spacing:F3} м должен быть меньше 0.8 м");
        }

        [Fact]
        public void DenseSteps_ClampedForPerformance()
        {
            // Верхняя граница 40 не даёт взрыву числа сэмплов на крупных ячейках.
            Assert.Equal(40, DenseOverlapSteps(1000.0, 0.05));
            // Нижняя граница 4 — не грубее прежней 3×3 сетки.
            Assert.Equal(4, DenseOverlapSteps(0.1, 0.05));
        }
    }
}
