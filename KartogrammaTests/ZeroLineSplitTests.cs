using System;
using KartogrammaPlugin;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Разделение объёма по нулевой линии (KartogrammaPlugin.ZeroLineSplit).
    //
    //  Квадрат, через который проходит линия нулевых работ, содержит и
    //  насыпь, и выемку. Сальдо их скрывает («+5 и −4» → «+1»), поэтому
    //  объёмы считаются раздельно.
    //
    //  Главные инварианты, которые здесь проверяются:
    //    • насыпь + выемка = полный объём фигуры (сальдо не теряется);
    //    • площадь насыпи + площадь выемки = площадь фигуры;
    //    • насыпь всегда ≥ 0, выемка всегда ≤ 0;
    //    • однородная фигура целиком уходит в свою сторону.
    // ═══════════════════════════════════════════════════════════════
    public class ZeroLineSplitTests
    {
        private const double Eps = 1e-9;

        // ── Однородные фигуры: нулевая линия не пересекает ────────────

        [Fact]
        public void AllPositive_IsPureFill()
        {
            ZeroLineSplit.Triangle(6.0, 1.0, 2.0, 3.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(6.0 * 2.0, f, 9);   // S·(1+2+3)/3 = 6·2
            Assert.Equal(0.0, c, 9);
            Assert.Equal(6.0, af, 9);
            Assert.Equal(0.0, ac, 9);
        }

        [Fact]
        public void AllNegative_IsPureCut()
        {
            ZeroLineSplit.Triangle(6.0, -1.0, -2.0, -3.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(0.0, f, 9);
            Assert.Equal(-6.0 * 2.0, c, 9);
            Assert.Equal(0.0, af, 9);
            Assert.Equal(6.0, ac, 9);
        }

        [Fact]
        public void AllZero_GivesNothing()
        {
            ZeroLineSplit.Triangle(6.0, 0.0, 0.0, 0.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(0.0, f, 9);
            Assert.Equal(0.0, c, 9);
            // Нулевые отметки — формально «не выемка», площадь уходит в насыпь;
            // объём при этом нулевой, на итоги не влияет.
            Assert.Equal(6.0, af + ac, 9);
        }

        // ── Смешанные: точное разделение ──────────────────────────────

        [Fact]
        public void OnePositiveTwoNegative_SplitIsExact()
        {
            // h = (+2, −2, −2), S = 9.
            // Нулевая линия отсекает треугольник при плюсовой вершине.
            // t1 = t2 = 2/(2−(−2)) = 0.5 → S_насыпь = 9·0.25 = 2.25
            // V_насыпь = 2.25 · 2/3 = 1.5
            // V_полн   = 9 · (2−2−2)/3 = −6  → V_выемка = −6 − 1.5 = −7.5
            ZeroLineSplit.Triangle(9.0, 2.0, -2.0, -2.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(2.25, af, 9);
            Assert.Equal(6.75, ac, 9);
            Assert.Equal(1.5, f, 9);
            Assert.Equal(-7.5, c, 9);
        }

        [Fact]
        public void OneNegativeTwoPositive_SplitIsExact()
        {
            // Зеркальный случай: h = (−2, +2, +2), S = 9
            ZeroLineSplit.Triangle(9.0, -2.0, 2.0, 2.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(2.25, ac, 9);
            Assert.Equal(6.75, af, 9);
            Assert.Equal(-1.5, c, 9);
            Assert.Equal(7.5, f, 9);
        }

        [Fact]
        public void ZeroVertex_DoesNotBreakSplit()
        {
            // Одна вершина ровно на нулевой линии: h = (+3, 0, −3), S = 12.
            // Нулевая линия проходит через вершину и середину
            // противоположного ребра → ровно пополам.
            ZeroLineSplit.Triangle(12.0, 3.0, 0.0, -3.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(6.0, af, 9);
            Assert.Equal(6.0, ac, 9);
            Assert.Equal(6.0, f, 9);    // 6 · (3+0+0)/3
            Assert.Equal(-6.0, c, 9);
            Assert.Equal(0.0, f + c, 9); // симметрия → сальдо ноль
        }

        // ── Инварианты на произвольных данных ─────────────────────────

        [Theory]
        [InlineData(1.0, -1.0, 2.0)]
        [InlineData(-5.0, 0.25, 3.0)]
        [InlineData(0.001, -7.0, 7.0)]
        [InlineData(-0.5, -0.5, 0.5)]
        [InlineData(10.0, -0.0001, 0.0)]
        [InlineData(-3.0, 4.0, -1.0)]
        public void SumOfPartsEqualsWhole(double h1, double h2, double h3)
        {
            const double S = 7.3;
            ZeroLineSplit.Triangle(S, h1, h2, h3,
                out double f, out double c, out double af, out double ac);

            double whole = S * (h1 + h2 + h3) / 3.0;

            Assert.Equal(whole, f + c, 9);
            Assert.Equal(S, af + ac, 9);
            Assert.True(f >= -Eps, $"насыпь должна быть ≥ 0, получено {f}");
            Assert.True(c <= Eps,  $"выемка должна быть ≤ 0, получено {c}");
            Assert.True(af >= -Eps && ac >= -Eps, "площади не могут быть отрицательными");
        }

        [Fact]
        public void SplitMatchesAnalyticWedge()
        {
            // Клин: h меняется линейно от −1 до +1 по треугольнику (−1,−1,+1).
            // t = 1/(1−(−1)) = 0.5 для обоих рёбер от плюсовой вершины
            // → S_насыпь = S/4, V_насыпь = (S/4)·(1/3) = S/12
            // V_полн = S·(−1−1+1)/3 = −S/3 → V_выемка = −S/3 − S/12 = −5S/12
            const double S = 12.0;
            ZeroLineSplit.Triangle(S, -1.0, -1.0, 1.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(S / 4.0, af, 9);
            Assert.Equal(S / 12.0, f, 9);
            Assert.Equal(-5.0 * S / 12.0, c, 9);
            Assert.Equal(3.0 * S / 4.0, ac, 9);
        }

        // ── Вырожденные входные данные ────────────────────────────────

        [Fact]
        public void ZeroArea_GivesNothing()
        {
            ZeroLineSplit.Triangle(0.0, 5.0, -5.0, 1.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(0.0, f, 9);
            Assert.Equal(0.0, c, 9);
            Assert.Equal(0.0, af, 9);
            Assert.Equal(0.0, ac, 9);
        }

        [Fact]
        public void NaNHeight_GivesNothing()
        {
            ZeroLineSplit.Triangle(5.0, double.NaN, -5.0, 1.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(0.0, f, 9);
            Assert.Equal(0.0, c, 9);
        }

        // ── Четырёхугольник (метод квадратов) ─────────────────────────

        [Fact]
        public void Quad_AllPositive_IsPureFill()
        {
            ZeroLineSplit.Quad(100.0, 1.0, 1.0, 1.0, 1.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(100.0, f, 9);
            Assert.Equal(0.0, c, 9);
            Assert.Equal(100.0, af, 9);
            Assert.Equal(0.0, ac, 9);
        }

        [Fact]
        public void Quad_HalfCutHalfFill_IsSymmetric()
        {
            // Отметки +1, +1, −1, −1 по обходу: нулевая линия делит клетку
            // пополам, объёмы симметричны, сальдо ноль.
            ZeroLineSplit.Quad(100.0, 1.0, 1.0, -1.0, -1.0,
                out double f, out double c, out double af, out double ac);

            Assert.Equal(0.0, f + c, 9);
            Assert.Equal(100.0, af + ac, 9);
            Assert.Equal(af, ac, 6);
            Assert.True(f > 0 && c < 0, "должны быть и насыпь, и выемка");
        }

        [Fact]
        public void Quad_SumOfPartsEqualsWhole()
        {
            const double S = 64.0;
            double h00 = 2.5, h01 = -1.0, h11 = -3.0, h10 = 0.5;

            ZeroLineSplit.Quad(S, h00, h01, h11, h10,
                out double f, out double c, out double af, out double ac);

            // Полный объём по той же диагональной разбивке
            double whole = S * 0.5 * (h00 + h01 + h11) / 3.0
                         + S * 0.5 * (h00 + h11 + h10) / 3.0;

            Assert.Equal(whole, f + c, 9);
            Assert.Equal(S, af + ac, 9);
        }

        // ── Определение «смешанной» ячейки ────────────────────────────

        [Theory]
        [InlineData(true,  1.0, -1.0)]
        [InlineData(false, 1.0, 2.0)]
        [InlineData(false, -1.0, -2.0)]
        [InlineData(false, 0.0, 0.0)]
        [InlineData(false, 0.0, 5.0)]
        [InlineData(true,  -0.001, 0.001)]
        public void IsMixed_DetectsBothSigns(bool expected, double a, double b)
        {
            Assert.Equal(expected, ZeroLineSplit.IsMixed(a, b));
        }

        [Fact]
        public void IsMixed_IgnoresMissingData()
        {
            Assert.False(ZeroLineSplit.IsMixed(double.NaN, 1.0, 2.0));
            Assert.True(ZeroLineSplit.IsMixed(double.NaN, 1.0, -2.0));
        }
    }
}
