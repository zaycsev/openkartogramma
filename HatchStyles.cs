using System;

namespace KartogrammaPlugin
{
    /// <summary>
    /// Настройки одной штриховки картограммы — выемки или насыпи.
    /// Штриховка накладывается на ту часть картограммы, где рабочая отметка
    /// соответствующего знака, и обрезается нулевой линией так же, как
    /// объёмы (см. ZeroLineSplit).
    /// </summary>
    public sealed class HatchSpec
    {
        /// <summary>Рисовать ли штриховку.</summary>
        public bool   Enabled  { get; set; }
        /// <summary>Цвет штриховки, ACI-код AutoCAD.</summary>
        public int    ColorAci { get; set; } = 1;
        /// <summary>Имя образца из палитры AutoCAD (acad.pat), например ANSI31.</summary>
        public string Pattern  { get; set; } = "ANSI31";
        /// <summary>Угол поворота образца, градусы.</summary>
        public double Angle    { get; set; }
        /// <summary>Масштаб образца.</summary>
        public double Scale    { get; set; } = 0.1;

        public HatchSpec Clone() => (HatchSpec)MemberwiseClone();

        public bool SameAs(HatchSpec o) =>
            Enabled == o.Enabled && ColorAci == o.ColorAci &&
            Pattern == o.Pattern && Math.Abs(Angle - o.Angle) < 1e-9 &&
            Math.Abs(Scale - o.Scale) < 1e-9;
    }

    /// <summary>
    /// Настройки линии нулевых работ — границы между насыпью и выемкой,
    /// где проектная поверхность пересекает существующую.
    /// </summary>
    public sealed class ZeroLineStyle
    {
        /// <summary>Рисовать ли линию.</summary>
        public bool   Enabled    { get; set; }
        /// <summary>Цвет линии, ACI-код AutoCAD.</summary>
        public int    ColorAci   { get; set; } = 2;
        /// <summary>Имя типа линий из чертежа.</summary>
        public string LineType   { get; set; } = "Continuous";
        /// <summary>Вес линии в миллиметрах; отрицательное значение — «по слою».</summary>
        public double LineWeight { get; set; } = -1.0;

        public ZeroLineStyle Clone() => (ZeroLineStyle)MemberwiseClone();

        public bool SameAs(ZeroLineStyle o) =>
            Enabled == o.Enabled && ColorAci == o.ColorAci &&
            LineType == o.LineType && Math.Abs(LineWeight - o.LineWeight) < 1e-9;

        /// <summary>Стандартный ряд весов линий AutoCAD, мм. −1 — «по слою».</summary>
        public static readonly double[] StandardWeights =
        {
            -1.0, 0.00, 0.05, 0.09, 0.13, 0.15, 0.18, 0.20, 0.25, 0.30,
            0.35, 0.40, 0.50, 0.53, 0.60, 0.70, 0.80, 0.90, 1.00, 1.06,
            1.20, 1.40, 1.58, 2.00, 2.11
        };

        public static string WeightText(double mm) =>
            mm < 0 ? "По слою" : mm.ToString("F2") + " мм";
    }
}
