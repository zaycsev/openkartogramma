using System;

namespace KartogrammaPlugin
{
    /// <summary>
    /// Разделение объёма по НУЛЕВОЙ ЛИНИИ — линии нулевых работ, где проектная
    /// поверхность пересекает существующую (рабочая отметка h = 0).
    ///
    /// Обычный квадрат картограммы целиком либо насыпь, либо выемка. Но там,
    /// где нулевая линия проходит через квадрат (склон котлована, гребень
    /// насыпи посреди выемки), в одном квадрате есть и то и другое, и объёмы
    /// нужно считать РАЗДЕЛЬНО: сальдо «+5 и −4 = +1» скрывает 9 м³ реально
    /// выполненных работ.
    ///
    /// На треугольнике рабочая отметка меняется ЛИНЕЙНО, поэтому разделение
    /// точное, без всякой численной аппроксимации:
    ///   • нулевая линия отсекает от треугольника меньший треугольник при той
    ///     вершине, чей знак отличается от двух других;
    ///   • отсечённый треугольник подобен исходному с коэффициентами
    ///     t1, t2 — долями рёбер до точек h = 0, значит его площадь равна
    ///     S·t1·t2 без вычисления самих точек;
    ///   • в двух его вершинах h = 0, поэтому объём там S_отс · h_вершины / 3;
    ///   • остаток считается разностью, поэтому насыпь и выемка в сумме
    ///     всегда дают исходный объём треугольника.
    ///
    /// Модуль намеренно не зависит от типов AutoCAD/Civil — он подключается
    /// к юнит-тестам напрямую (см. KartogrammaTests.csproj).
    /// </summary>
    internal static class ZeroLineSplit
    {
        /// <summary>
        /// Разделить треугольник с линейной рабочей отметкой на насыпную
        /// (h &gt; 0) и выемочную (h &lt; 0) части.
        /// </summary>
        /// <param name="area">Площадь треугольника в плане, м².</param>
        /// <param name="h1">Рабочая отметка в 1-й вершине (проектная − существующая).</param>
        /// <param name="h2">Рабочая отметка во 2-й вершине.</param>
        /// <param name="h3">Рабочая отметка в 3-й вершине.</param>
        /// <param name="volFill">Объём насыпи, ≥ 0.</param>
        /// <param name="volCut">Объём выемки, ≤ 0 (знак сохранён).</param>
        /// <param name="areaFill">Площадь насыпной части в плане, м².</param>
        /// <param name="areaCut">Площадь выемочной части в плане, м².</param>
        internal static void Triangle(
            double area, double h1, double h2, double h3,
            out double volFill, out double volCut,
            out double areaFill, out double areaCut)
        {
            volFill = volCut = areaFill = areaCut = 0.0;
            if (!(area > 0.0)) return;
            if (double.IsNaN(h1) || double.IsNaN(h2) || double.IsNaN(h3)) return;

            double total = area * (h1 + h2 + h3) / 3.0;

            bool anyPos = h1 > 0.0 || h2 > 0.0 || h3 > 0.0;
            bool anyNeg = h1 < 0.0 || h2 < 0.0 || h3 < 0.0;

            // Однородный треугольник — нулевая линия его не пересекает.
            // (Случай «все нули» попадает сюда же: объём и так нулевой.)
            if (!anyNeg) { volFill = total; areaFill = area; return; }
            if (!anyPos) { volCut  = total; areaCut  = area; return; }

            // ── Смешанный: ищем «одинокую» вершину ────────────────────────────
            // При трёх вершинах со смешанными знаками одиночным всегда
            // оказывается либо единственный плюс, либо единственный минус.
            int pos = (h1 > 0 ? 1 : 0) + (h2 > 0 ? 1 : 0) + (h3 > 0 ? 1 : 0);

            double hLone, hOther1, hOther2;
            bool   loneIsFill;
            if (pos == 1)
            {
                loneIsFill = true;
                if      (h1 > 0) { hLone = h1; hOther1 = h2; hOther2 = h3; }
                else if (h2 > 0) { hLone = h2; hOther1 = h1; hOther2 = h3; }
                else             { hLone = h3; hOther1 = h1; hOther2 = h2; }
            }
            else
            {
                // Минус ровно один (иначе плюсов было бы не больше одного).
                loneIsFill = false;
                if      (h1 < 0) { hLone = h1; hOther1 = h2; hOther2 = h3; }
                else if (h2 < 0) { hLone = h2; hOther1 = h1; hOther2 = h3; }
                else             { hLone = h3; hOther1 = h1; hOther2 = h2; }
            }

            // Доли рёбер от одинокой вершины до точек h = 0. Знаменатель не
            // обращается в ноль: у hOther знак противоположный либо он нулевой,
            // и тогда доля равна 1 — точка совпадает с самой вершиной.
            double t1 = hLone / (hLone - hOther1);
            double t2 = hLone / (hLone - hOther2);

            // Отсечённый треугольник подобен исходному → S·t1·t2.
            double areaLone = area * t1 * t2;
            double volLone  = areaLone * hLone / 3.0;   // в двух вершинах h = 0

            if (loneIsFill)
            {
                volFill  = volLone;         areaFill = areaLone;
                volCut   = total - volLone; areaCut  = area - areaLone;
            }
            else
            {
                volCut   = volLone;         areaCut  = areaLone;
                volFill  = total - volLone; areaFill = area - areaLone;
            }
        }

        /// <summary>
        /// То же для четырёхугольника, заданного отметками в углах по обходу
        /// (h00 → h01 → h11 → h10). Делится диагональю на два треугольника
        /// равной площади — так работает классический ручной метод квадратов
        /// для клеток, пересечённых нулевой линией.
        /// </summary>
        internal static void Quad(
            double area, double h00, double h01, double h11, double h10,
            out double volFill, out double volCut,
            out double areaFill, out double areaCut)
        {
            Triangle(area * 0.5, h00, h01, h11,
                out double f1, out double c1, out double af1, out double ac1);
            Triangle(area * 0.5, h00, h11, h10,
                out double f2, out double c2, out double af2, out double ac2);

            volFill  = f1  + f2;
            volCut   = c1  + c2;
            areaFill = af1 + af2;
            areaCut  = ac1 + ac2;
        }

        /// <summary>
        /// Пересекает ли нулевая линия набор отметок, то есть есть ли и насыпь,
        /// и выемка одновременно. NaN (нет данных) игнорируются.
        /// </summary>
        internal static bool IsMixed(params double[] h)
        {
            bool pos = false, neg = false;
            foreach (var v in h)
            {
                if (double.IsNaN(v)) continue;
                if (v > 0) pos = true;
                else if (v < 0) neg = true;
                if (pos && neg) return true;
            }
            return false;
        }
    }
}
