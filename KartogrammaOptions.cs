using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace KartogrammaPlugin
{
    /// <summary>Метод расчёта объёма в ячейке.</summary>
    public enum VolumeMethod
    {
        /// <summary>Триангуляция — субтреугольное интегрирование поверхностей (точно).</summary>
        Triangulation = 0,
        /// <summary>Квадраты — S × (h1+h2+h3+h4)/4 по отметкам в узлах (классический ручной метод).</summary>
        Squares       = 1,
    }

    public class KartogrammaOptions
    {
        // ── Поверхности ─────────────────────────────────────────────────────────
        public string ExistingSurfaceName { get; set; } = "";
        public string DesignSurfaceName   { get; set; } = "";

        // ── Сетка ───────────────────────────────────────────────────────────────
        public double CellSizeX     { get; set; } = 1.0;
        public double CellSizeY     { get; set; } = 1.0;
        public bool   AutoBasePoint { get; set; } = true;
        public double BaseX         { get; set; } = 0.0;
        public double BaseY         { get; set; } = 0.0;
        public bool   DontClipCells { get; set; } = false;

        // ── Границы ────────────────────────────────────────────────────────────
        public bool     AutoBounds      { get; set; } = true;
        public ObjectId OuterBoundaryId { get; set; } = ObjectId.Null;
        /// <summary>Внутренние «дырки» — закрытые области, где сетка НЕ рисуется.</summary>
        public List<ObjectId> InnerBoundaryIds { get; set; } = new();

        // ── Поворот ─────────────────────────────────────────────────────────────
        public double RotationDegrees { get; set; } = 0.0;
        public double RotationRadians => RotationDegrees * Math.PI / 180.0;

        // ── Подписи (Отметки) ───────────────────────────────────────────────────
        public string TextStyleName    { get; set; } = "Standard";
        public double LargeTextHeight  { get; set; } = 0.15;
        public double SmallTextHeight  { get; set; } = 0.45;
        public int    TextPrecision    { get; set; } = 2;
        public string DecimalSeparator { get; set; } = ".";
        public bool   HideMaskText     { get; set; } = true;
        public bool   HideMaskVolume   { get; set; } = true;
        public bool   HideMaskTable    { get; set; } = false;
        public bool   Annotative       { get; set; } = false;

        // ── Цвета (ACI-коды AutoCAD) ────────────────────────────────────────────
        public int ColorExisting  { get; set; } = 5;   // синий  — чёрная отметка
        public int ColorDesign    { get; set; } = 1;   // красный — красная отметка
        public int ColorWork      { get; set; } = 3;   // зелёный — разница отметок
        public int ColorVolume    { get; set; } = 7;   // белый — объём
        public int ColorTable     { get; set; } = 7;   // белый — итоговая таблица

        // ── Итоговая таблица ────────────────────────────────────────────────────
        /// <summary>0=Сверху  1=Снизу  2=Слева  3=Справа</summary>
        public int    TablePosition      { get; set; } = 0;
        /// <summary>Высота шрифта в итоговой таблице, м</summary>
        public double TableTextHeight    { get; set; } = 0.25;
        /// <summary>Стиль текста для итоговой таблицы</summary>
        public string TableTextStyleName { get; set; } = "Standard";

        // ── Слои ────────────────────────────────────────────────────────────────
        public string GridLayerName   { get; set; } = "Картограмма сетка";
        public string TextLayerName   { get; set; } = "Картограмма текст";
        public string WorkLayerName   { get; set; } = "Картограмма разница";
        public string ExistLayerName  { get; set; } = "Картограмма черная";
        public string DesignLayerName { get; set; } = "Картограмма красная";
        public string VolumeLayerName { get; set; } = "Картограмма объём";
        public string TableLayerName  { get; set; } = "Картограмма таблица";

        // ── Объём / Вычисление ──────────────────────────────────────────────────
        public bool   DrawSummaryTable   { get; set; } = true;
        public double MinVolume          { get; set; } = 0.01;
        public double VolumeTextHeight   { get; set; } = 0.25;
        public int    VolumePrecision    { get; set; } = 2;

        /// <summary>
        /// Шаг субсетки для точного расчёта граничных объёмов, в метрах.
        /// N = ceil(CellSize / VolumeNodeStep), минимум 4.
        /// Меньше шаг → точнее граница, но чуть медленнее.
        ///   0.10 м → для ячеек 5×5:  N=50,  для 1×1: N=10, для 0.5×0.5: N=5
        ///   0.05 м → в 4 раза точнее, производительность всё ещё ок.
        /// Рекомендуется: 0.05–0.10 м.
        /// </summary>
        public double VolumeNodeStep { get; set; } = 0.05;

        /// <summary>Метод расчёта объёма: триангуляция или квадраты.</summary>
        public VolumeMethod VolumeMethod { get; set; } = VolumeMethod.Triangulation;
    }
}
