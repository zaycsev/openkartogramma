using System;
using System.Collections.Generic;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Легковесная замена Autodesk.AutoCAD.Geometry.Point2d
    //  (чтобы тесты не требовали AutoCAD runtime)
    // ═══════════════════════════════════════════════════════════════
    public readonly struct Pt2d
    {
        public readonly double X, Y;
        public Pt2d(double x, double y) { X = x; Y = y; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Копия геометрических алгоритмов из KartogrammaProcessor.cs
    //  (идентичная логика, но без зависимости от AutoCAD DLL)
    // ═══════════════════════════════════════════════════════════════
    public enum CellClass { Inside, Outside, Partial }

    public static class Geo
    {
        /// <summary>Точка внутри полигона — ray-casting (копия KartogrammaProcessor.PointInPolygon)</summary>
        public static bool PointInPolygon(Pt2d pt, List<Pt2d> poly)
        {
            int n = poly.Count;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = poly[i].Y, yj = poly[j].Y;
                double xi = poly[i].X, xj = poly[j].X;
                if (((yi > pt.Y) != (yj > pt.Y)) &&
                    (pt.X < (xj - xi) * (pt.Y - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Пересечение двух отрезков (копия KartogrammaProcessor.SegmentIntersect)</summary>
        public static bool SegmentIntersect(
            Pt2d a, Pt2d b, Pt2d c, Pt2d d,
            out double t, out Pt2d hit)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double ex = d.X - c.X, ey = d.Y - c.Y;
            double denom = dx * ey - dy * ex;
            t = 0; hit = a;
            if (Math.Abs(denom) < 1e-12) return false;
            double s = ((c.X - a.X) * ey - (c.Y - a.Y) * ex) / denom;
            double u = ((c.X - a.X) * dy - (c.Y - a.Y) * dx) / denom;
            if (s < -1e-9 || s > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return false;
            t = s;
            hit = new Pt2d(a.X + dx * s, a.Y + dy * s);
            return true;
        }

        /// <summary>Классифицировать ячейку относительно границы (копия KartogrammaProcessor.ClassifyCell)</summary>
        public static CellClass ClassifyCell(Pt2d[] corners, List<Pt2d> boundary)
        {
            int inside = 0;
            foreach (var pt in corners)
                if (PointInPolygon(pt, boundary)) inside++;

            if (inside == 4) return CellClass.Inside;
            if (inside > 0)  return CellClass.Partial;

            // Все 4 угла снаружи, но граница может пересекать ячейку
            for (int i = 0; i < boundary.Count; i++)
            {
                int j = (i + 1) % boundary.Count;
                for (int k = 0; k < 4; k++)
                {
                    int l = (k + 1) % 4;
                    if (SegmentIntersect(boundary[i], boundary[j], corners[k], corners[l],
                            out _, out _))
                        return CellClass.Partial;
                }
            }

            // Граница может быть полностью внутри ячейки
            if (boundary.Count > 0)
            {
                double minX = corners[0].X, maxX = corners[0].X;
                double minY = corners[0].Y, maxY = corners[0].Y;
                for (int i = 1; i < corners.Length; i++)
                {
                    if (corners[i].X < minX) minX = corners[i].X;
                    if (corners[i].X > maxX) maxX = corners[i].X;
                    if (corners[i].Y < minY) minY = corners[i].Y;
                    if (corners[i].Y > maxY) maxY = corners[i].Y;
                }
                foreach (var bp in boundary)
                {
                    if (bp.X > minX && bp.X < maxX && bp.Y > minY && bp.Y < maxY)
                        return CellClass.Partial;
                }
            }

            return CellClass.Outside;
        }
    }

    /// <summary>
    /// Тесты геометрических функций: PointInPolygon, SegmentIntersect, ClassifyCell.
    /// Алгоритмы идентичны KartogrammaProcessor — тестируем логику без AutoCAD runtime.
    /// </summary>
    public class GeometryTests
    {
        // ═══════════════════════════════════════════════════════════════
        //  Фабрики полигонов
        // ═══════════════════════════════════════════════════════════════

        static List<Pt2d> Square10() => new List<Pt2d>
        {
            new Pt2d(0, 0), new Pt2d(10, 0),
            new Pt2d(10, 10), new Pt2d(0, 10)
        };

        static List<Pt2d> LShape() => new List<Pt2d>
        {
            new Pt2d(0, 0), new Pt2d(10, 0),
            new Pt2d(10, 5), new Pt2d(5, 5),
            new Pt2d(5, 10), new Pt2d(0, 10)
        };

        static List<Pt2d> Triangle() => new List<Pt2d>
        {
            new Pt2d(0, 0), new Pt2d(10, 0), new Pt2d(5, 10)
        };

        static List<Pt2d> BigSquare() => new List<Pt2d>
        {
            new Pt2d(0, 0), new Pt2d(100, 0),
            new Pt2d(100, 100), new Pt2d(0, 100)
        };

        static Pt2d[] CellCorners(double x, double y, double sx, double sy) => new[]
        {
            new Pt2d(x, y), new Pt2d(x + sx, y),
            new Pt2d(x + sx, y + sy), new Pt2d(x, y + sy)
        };

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: выпуклый полигон (квадрат)
        // ═══════════════════════════════════════════════════════════════

        [Fact] public void PIP_CenterOfSquare_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(5, 5), Square10()));

        [Fact] public void PIP_OutsideSquare_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(15, 5), Square10()));

        [Fact] public void PIP_FarOutside_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(-100, -100), Square10()));

        [Fact] public void PIP_NearCorner_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(0.001, 0.001), Square10()));

        [Fact] public void PIP_JustOutsideLeft_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(-0.001, 5), Square10()));

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: невыпуклый полигон (L-shape)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void PIP_InsideLShape_BottomPart_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(7, 2), LShape()));

        [Fact]
        public void PIP_InsideLShape_LeftTopPart_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(2, 7), LShape()));

        [Fact]
        public void PIP_InConcavityOfLShape_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(7, 7), LShape()));

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: треугольник
        // ═══════════════════════════════════════════════════════════════

        [Fact] public void PIP_InsideTriangle_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(5, 3), Triangle()));

        [Fact] public void PIP_OutsideTriangle_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(9, 8), Triangle()));

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: CW vs CCW (ray-casting инвариантен)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void PIP_CW_SameAsCCW()
        {
            var ccw = Square10();
            var cw = new List<Pt2d>(ccw); cw.Reverse();
            var ptIn = new Pt2d(5, 5);
            var ptOut = new Pt2d(15, 5);
            Assert.Equal(Geo.PointInPolygon(ptIn, ccw), Geo.PointInPolygon(ptIn, cw));
            Assert.Equal(Geo.PointInPolygon(ptOut, ccw), Geo.PointInPolygon(ptOut, cw));
        }

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: сканирование сеткой
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void PIP_GridScan_Square()
        {
            var poly = Square10();
            int cnt = 0;
            for (double x = 0.5; x < 20; x += 1)
            for (double y = 0.5; y < 20; y += 1)
                if (Geo.PointInPolygon(new Pt2d(x, y), poly)) cnt++;
            Assert.Equal(100, cnt);
        }

        [Fact]
        public void PIP_GridScan_LShape()
        {
            var poly = LShape();
            int cnt = 0;
            for (double x = 0.5; x < 10; x += 1)
            for (double y = 0.5; y < 10; y += 1)
                if (Geo.PointInPolygon(new Pt2d(x, y), poly)) cnt++;
            // L = нижняя 10×5 + верхняя 5×5 = 75
            Assert.Equal(75, cnt);
        }

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: краевые случаи
        // ═══════════════════════════════════════════════════════════════

        [Fact] public void PIP_EmptyPolygon_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(5, 5), new List<Pt2d>()));

        [Fact] public void PIP_SinglePoint_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(5, 5), new List<Pt2d> { new Pt2d(5, 5) }));

        [Fact] public void PIP_TwoPoints_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(5, 5),
                new List<Pt2d> { new Pt2d(0, 0), new Pt2d(10, 10) }));

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: большие координаты (реальные геодезические)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void PIP_LargeCoordinates()
        {
            var poly = new List<Pt2d>
            {
                new Pt2d(45000, 23000), new Pt2d(45200, 23000),
                new Pt2d(45200, 23150), new Pt2d(45000, 23150)
            };
            Assert.True(Geo.PointInPolygon(new Pt2d(45100, 23075), poly));
            Assert.False(Geo.PointInPolygon(new Pt2d(44000, 23075), poly));
        }

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: звёздообразный (сильно невыпуклый) полигон
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void PIP_StarShape()
        {
            // 5-лучевая звезда с центром (50,50), внешний радиус 40, внутренний 15
            var star = new List<Pt2d>();
            for (int i = 0; i < 10; i++)
            {
                double angle = Math.PI / 2 + i * Math.PI / 5;
                double r = (i % 2 == 0) ? 40 : 15;
                star.Add(new Pt2d(50 + r * Math.Cos(angle), 50 + r * Math.Sin(angle)));
            }
            // Центр — внутри
            Assert.True(Geo.PointInPolygon(new Pt2d(50, 50), star));
            // Точка далеко от звезды — снаружи
            Assert.False(Geo.PointInPolygon(new Pt2d(200, 200), star));
        }

        // ═══════════════════════════════════════════════════════════════
        //  SegmentIntersect
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void SI_CrossingSegments()
        {
            bool r = Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(10, 10),
                new Pt2d(0, 10), new Pt2d(10, 0),
                out double t, out Pt2d hit);
            Assert.True(r);
            Assert.InRange(t, 0.49, 0.51);
            Assert.InRange(hit.X, 4.99, 5.01);
            Assert.InRange(hit.Y, 4.99, 5.01);
        }

        [Fact]
        public void SI_Parallel_False()
        {
            Assert.False(Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(10, 0),
                new Pt2d(0, 1), new Pt2d(10, 1), out _, out _));
        }

        [Fact]
        public void SI_NonCrossing_False()
        {
            Assert.False(Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(5, 0),
                new Pt2d(6, 1), new Pt2d(10, 5), out _, out _));
        }

        [Fact]
        public void SI_TShape()
        {
            bool r = Geo.SegmentIntersect(
                new Pt2d(0, 5), new Pt2d(10, 5),
                new Pt2d(5, 0), new Pt2d(5, 10),
                out _, out Pt2d hit);
            Assert.True(r);
            Assert.InRange(hit.X, 4.99, 5.01);
            Assert.InRange(hit.Y, 4.99, 5.01);
        }

        [Fact]
        public void SI_EndpointTouch()
        {
            bool r = Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(5, 5),
                new Pt2d(5, 5), new Pt2d(10, 0),
                out _, out Pt2d hit);
            Assert.True(r);
            Assert.InRange(hit.X, 4.99, 5.01);
        }

        [Fact]
        public void SI_Collinear_False()
        {
            Assert.False(Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(5, 0),
                new Pt2d(3, 0), new Pt2d(8, 0), out _, out _));
        }

        [Fact]
        public void SI_AlmostParallel_False()
        {
            // Два отрезка почти параллельных, но не пересекающихся
            Assert.False(Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(10, 0.0001),
                new Pt2d(0, 5), new Pt2d(10, 5.0001), out _, out _));
        }

        [Fact]
        public void SI_DisjointSegments_False()
        {
            // Отрезки далеко друг от друга
            Assert.False(Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(1, 1),
                new Pt2d(100, 100), new Pt2d(101, 101), out _, out _));
        }

        // ═══════════════════════════════════════════════════════════════
        //  ClassifyCell
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void CC_FullyInside()
            => Assert.Equal(CellClass.Inside,
                Geo.ClassifyCell(CellCorners(20, 20, 10, 10), BigSquare()));

        [Fact]
        public void CC_FullyOutside()
            => Assert.Equal(CellClass.Outside,
                Geo.ClassifyCell(CellCorners(200, 200, 10, 10), BigSquare()));

        [Fact]
        public void CC_OnEdge_Partial()
            => Assert.Equal(CellClass.Partial,
                Geo.ClassifyCell(CellCorners(95, 50, 10, 10), BigSquare()));

        [Fact]
        public void CC_OnCorner_Partial()
            => Assert.Equal(CellClass.Partial,
                Geo.ClassifyCell(CellCorners(95, 95, 10, 10), BigSquare()));

        [Fact]
        public void CC_BoundaryPassesThrough_AllCornersOutside_Partial()
        {
            // Узкая граница проходит вертикально через широкую ячейку
            var boundary = new List<Pt2d>
            {
                new Pt2d(4, 0), new Pt2d(6, 0),
                new Pt2d(6, 20), new Pt2d(4, 20)
            };
            Assert.Equal(CellClass.Partial,
                Geo.ClassifyCell(CellCorners(0, 5, 10, 10), boundary));
        }

        [Fact]
        public void CC_InsideLShape()
            => Assert.Equal(CellClass.Inside,
                Geo.ClassifyCell(CellCorners(1, 1, 2, 2), LShape()));

        [Fact]
        public void CC_InConcavityOfLShape_Outside()
            => Assert.Equal(CellClass.Outside,
                Geo.ClassifyCell(CellCorners(6, 6, 2, 2), LShape()));

        [Fact]
        public void CC_TinyCell_Inside()
            => Assert.Equal(CellClass.Inside,
                Geo.ClassifyCell(CellCorners(50, 50, 0.01, 0.01), BigSquare()));

        [Fact]
        public void CC_CellEnclosesBoundary_Partial()
        {
            var boundary = new List<Pt2d>
            {
                new Pt2d(4, 4), new Pt2d(6, 4),
                new Pt2d(6, 6), new Pt2d(4, 6)
            };
            Assert.Equal(CellClass.Partial,
                Geo.ClassifyCell(CellCorners(0, 0, 10, 10), boundary));
        }

        // ═══════════════════════════════════════════════════════════════
        //  ClassifyCell: ячейка касается границы одним ребром
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void CC_CellTouchesEdge_Partial()
        {
            // Ячейка ровно на правом краю BigSquare (90..100 x 40..60)
            // Два угла внутри, два на краю — зависит от алгоритма,
            // но точно не Outside
            var result = Geo.ClassifyCell(CellCorners(90, 40, 10, 20), BigSquare());
            Assert.NotEqual(CellClass.Outside, result);
        }

        // ═══════════════════════════════════════════════════════════════
        //  ClassifyCell: сетка ячеек по L-фигуре (интеграционный тест)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void CC_GridOverLShape_CountInsideAndOutside()
        {
            var boundary = LShape();
            int inside = 0, outside = 0, partial = 0;
            // Разбиваем 10×10 на ячейки 2×2 = 25 ячеек
            for (double x = 0; x < 10; x += 2)
            for (double y = 0; y < 10; y += 2)
            {
                var cls = Geo.ClassifyCell(CellCorners(x, y, 2, 2), boundary);
                switch (cls)
                {
                    case CellClass.Inside:  inside++;  break;
                    case CellClass.Outside: outside++; break;
                    case CellClass.Partial: partial++; break;
                }
            }
            // L-фигура покрывает 75% площади (75/100), остальное — вырез 5×5 в правом верхнем углу
            // Вырез: x=5..10, y=5..10 → ячейки (5,5),(5,7),(5,9),(7,5),(7,7),(7,9),(9,5),(9,7),(9,9)
            // Но x=5, y=5 — это граница... Пограничные ячейки будут Partial
            Assert.True(inside > 0, "Должны быть ячейки полностью внутри");
            Assert.True(outside > 0, "Должны быть ячейки полностью снаружи");
            // inside + outside + partial = 25 (всего ячеек в сетке 5×5)
            Assert.Equal(25, inside + outside + partial);
        }

        // ═══════════════════════════════════════════════════════════════
        //  PointInPolygon: точка на горизонтальном ребре (edge case)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void PIP_OnHorizontalEdge_ConsistentBehavior()
        {
            var poly = Square10();
            // Точка на нижнем ребре (y=0) — поведение ray-casting на ребре
            // может вернуть true или false, но не должна кинуть исключение
            var pt = new Pt2d(5, 0);
            // Просто проверяем, что не падает
            _ = Geo.PointInPolygon(pt, poly);
        }

        [Fact]
        public void PIP_OnVerticalEdge_ConsistentBehavior()
        {
            var poly = Square10();
            var pt = new Pt2d(0, 5);
            _ = Geo.PointInPolygon(pt, poly);
        }

        // ═══════════════════════════════════════════════════════════════
        //  SegmentIntersect: zero-length segment
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public void SI_ZeroLengthSegment_ReturnsFalse()
        {
            // Вырожденный отрезок (точка) — denom будет 0
            Assert.False(Geo.SegmentIntersect(
                new Pt2d(5, 5), new Pt2d(5, 5),
                new Pt2d(0, 0), new Pt2d(10, 10), out _, out _));
        }

        // ═══════════════════════════════════════════════════════════════
        //  РАУНД 2: дополнительные сценарии
        // ═══════════════════════════════════════════════════════════════

        // --- PIP: ромбовидная граница (поворот 45°) ---

        static List<Pt2d> Diamond() => new List<Pt2d>
        {
            new Pt2d(50, 0), new Pt2d(100, 50),
            new Pt2d(50, 100), new Pt2d(0, 50)
        };

        [Fact] public void PIP_CenterOfDiamond_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(50, 50), Diamond()));

        [Fact] public void PIP_CornerRegionOfDiamond_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(5, 5), Diamond()));

        [Fact] public void PIP_InsideDiamond_NearEdge_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(50, 1), Diamond()));

        // --- PIP: U-образный полигон (ещё один невыпуклый) ---

        static List<Pt2d> UShape() => new List<Pt2d>
        {
            new Pt2d(0, 0), new Pt2d(10, 0), new Pt2d(10, 10),
            new Pt2d(8, 10), new Pt2d(8, 3), new Pt2d(2, 3),
            new Pt2d(2, 10), new Pt2d(0, 10)
        };

        [Fact] public void PIP_InsideUShape_Bottom_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(5, 1), UShape()));

        [Fact] public void PIP_InsideUShape_LeftWall_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(1, 7), UShape()));

        [Fact] public void PIP_InsideUShape_RightWall_True()
            => Assert.True(Geo.PointInPolygon(new Pt2d(9, 7), UShape()));

        [Fact] public void PIP_InOpeningOfU_False()
            => Assert.False(Geo.PointInPolygon(new Pt2d(5, 7), UShape()));

        // --- PIP: GridScan по U-фигуре ---

        [Fact]
        public void PIP_GridScan_UShape()
        {
            var poly = UShape();
            int cnt = 0;
            for (double x = 0.5; x < 10; x += 1)
            for (double y = 0.5; y < 10; y += 1)
                if (Geo.PointInPolygon(new Pt2d(x, y), poly)) cnt++;
            // U = полный прямоугольник 10×10 минус вырез 6×7 (x=2..8, y=3..10)
            // 100 - 42 = 58
            Assert.Equal(58, cnt);
        }

        // --- SI: пересечение под острым углом ---

        [Fact]
        public void SI_AcuteAngle()
        {
            bool r = Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(100, 1),
                new Pt2d(50, -5), new Pt2d(50, 5),
                out _, out Pt2d hit);
            Assert.True(r);
            Assert.InRange(hit.X, 49.9, 50.1);
        }

        // --- SI: пересечение на самом конце отрезка ---

        [Fact]
        public void SI_IntersectionAtEnd()
        {
            bool r = Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(10, 0),
                new Pt2d(10, -5), new Pt2d(10, 5),
                out double t, out _);
            Assert.True(r);
            Assert.InRange(t, 0.99, 1.01);
        }

        // --- SI: пересечение на самом начале отрезка ---

        [Fact]
        public void SI_IntersectionAtStart()
        {
            bool r = Geo.SegmentIntersect(
                new Pt2d(0, 0), new Pt2d(10, 0),
                new Pt2d(0, -5), new Pt2d(0, 5),
                out double t, out _);
            Assert.True(r);
            Assert.InRange(t, -0.01, 0.01);
        }

        // --- CC: ромбовидная граница с сеткой ---

        [Fact]
        public void CC_GridOverDiamond()
        {
            var boundary = Diamond();
            int inside = 0, outside = 0, partial = 0;
            for (double x = 0; x < 100; x += 10)
            for (double y = 0; y < 100; y += 10)
            {
                var cls = Geo.ClassifyCell(CellCorners(x, y, 10, 10), boundary);
                switch (cls)
                {
                    case CellClass.Inside:  inside++;  break;
                    case CellClass.Outside: outside++; break;
                    case CellClass.Partial: partial++; break;
                }
            }
            Assert.Equal(100, inside + outside + partial);
            Assert.True(inside > 0);
            Assert.True(outside > 0);
            Assert.True(partial > 0);
        }

        // --- CC: маленькая граница в углу большой ячейки ---

        [Fact]
        public void CC_SmallBoundaryInCornerOfCell_Partial()
        {
            var boundary = new List<Pt2d>
            {
                new Pt2d(0.1, 0.1), new Pt2d(0.5, 0.1),
                new Pt2d(0.5, 0.5), new Pt2d(0.1, 0.5)
            };
            Assert.Equal(CellClass.Partial,
                Geo.ClassifyCell(CellCorners(0, 0, 100, 100), boundary));
        }

        // --- CC: ячейка вплотную снаружи (epsilon gap) ---

        [Fact]
        public void CC_CellJustOutside()
        {
            var boundary = Square10();
            // Ячейка начинается сразу за правой границей
            Assert.Equal(CellClass.Outside,
                Geo.ClassifyCell(CellCorners(10.001, 0, 5, 5), boundary));
        }

        // --- CC: U-shape с ячейкой в вырезе ---

        [Fact]
        public void CC_UShape_CellInOpening_Outside()
        {
            var boundary = UShape();
            // Ячейка 3..7 x 4..8 — внутри «выреза» U
            Assert.Equal(CellClass.Outside,
                Geo.ClassifyCell(CellCorners(3, 4, 4, 4), boundary));
        }

        [Fact]
        public void CC_UShape_CellInBottom_Inside()
        {
            var boundary = UShape();
            // Ячейка 3..7 x 0.5..2.5 — внутри дна U
            Assert.Equal(CellClass.Inside,
                Geo.ClassifyCell(CellCorners(3, 0.5, 4, 2), boundary));
        }

        // --- PIP: стресс-тест с большим количеством вершин (окружность) ---

        [Fact]
        public void PIP_CirclePolygon_1000Vertices()
        {
            var poly = new List<Pt2d>();
            int n = 1000;
            double cx = 500, cy = 500, r = 200;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                poly.Add(new Pt2d(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
            // Центр — внутри
            Assert.True(Geo.PointInPolygon(new Pt2d(cx, cy), poly));
            // Точка на расстоянии r-1 — внутри
            Assert.True(Geo.PointInPolygon(new Pt2d(cx + r - 1, cy), poly));
            // Точка на расстоянии r+1 — снаружи
            Assert.False(Geo.PointInPolygon(new Pt2d(cx + r + 1, cy), poly));
            // Точка далеко — снаружи
            Assert.False(Geo.PointInPolygon(new Pt2d(0, 0), poly));
        }

        // --- CC: повёрнутая ячейка (имитация поворота сетки) ---

        [Fact]
        public void CC_RotatedCell_InsideBigSquare()
        {
            // Ячейка повёрнута на 45° — не axis-aligned
            double cx = 50, cy = 50, half = 5;
            double cos45 = Math.Cos(Math.PI / 4), sin45 = Math.Sin(Math.PI / 4);
            var corners = new[]
            {
                new Pt2d(cx + half * (-cos45 - (-sin45)), cy + half * (-sin45 + (-cos45))),
                new Pt2d(cx + half * ( cos45 - (-sin45)), cy + half * ( sin45 + (-cos45))),
                new Pt2d(cx + half * ( cos45 - ( sin45)), cy + half * ( sin45 + ( cos45))),
                new Pt2d(cx + half * (-cos45 - ( sin45)), cy + half * (-sin45 + ( cos45)))
            };
            Assert.Equal(CellClass.Inside, Geo.ClassifyCell(corners, BigSquare()));
        }

        // --- Числовая стабильность: очень большие координаты ---

        [Fact]
        public void PIP_VeryLargeCoords()
        {
            double bx = 1e7, by = 1e7;
            var poly = new List<Pt2d>
            {
                new Pt2d(bx, by), new Pt2d(bx + 100, by),
                new Pt2d(bx + 100, by + 100), new Pt2d(bx, by + 100)
            };
            Assert.True(Geo.PointInPolygon(new Pt2d(bx + 50, by + 50), poly));
            Assert.False(Geo.PointInPolygon(new Pt2d(bx - 1, by + 50), poly));
        }

        [Fact]
        public void SI_VeryLargeCoords()
        {
            double bx = 1e7;
            bool r = Geo.SegmentIntersect(
                new Pt2d(bx, 0), new Pt2d(bx + 10, 10),
                new Pt2d(bx, 10), new Pt2d(bx + 10, 0),
                out _, out Pt2d hit);
            Assert.True(r);
            Assert.InRange(hit.X, bx + 4.99, bx + 5.01);
        }

        // ═══════════════════════════════════════════════════════════════
        //  РАУНД 3: стресс-тесты и финальные проверки
        // ═══════════════════════════════════════════════════════════════

        // --- Полная сетка: все ячейки Inside/Partial/Outside суммарно дают корректную площадь ---

        [Fact]
        public void CC_FullGrid_AreaConsistency_Square()
        {
            // Граница 50×50, ячейки 10×10, сетка 100×100
            var boundary = new List<Pt2d>
            {
                new Pt2d(25, 25), new Pt2d(75, 25),
                new Pt2d(75, 75), new Pt2d(25, 75)
            };
            int inside = 0, partial = 0, outside = 0;
            for (double x = 0; x < 100; x += 10)
            for (double y = 0; y < 100; y += 10)
            {
                var cls = Geo.ClassifyCell(CellCorners(x, y, 10, 10), boundary);
                switch (cls)
                {
                    case CellClass.Inside:  inside++;  break;
                    case CellClass.Partial: partial++;  break;
                    case CellClass.Outside: outside++; break;
                }
            }
            // 10×10 = 100 ячеек
            Assert.Equal(100, inside + partial + outside);
            // Внутри 50×50 (25..75) → ячейки [30..70)×[30..70) полностью внутри = 4×4=16
            Assert.Equal(16, inside);
            // Пограничных ячеек: по периметру границы, пересекающих ребро
            Assert.True(partial > 0);
            // Снаружи: все остальные
            Assert.True(outside > 0);
        }

        // --- Тест: узкий длинный полигон (коридор) ---

        [Fact]
        public void PIP_NarrowCorridor()
        {
            // Коридор шириной 0.1, длиной 100
            var poly = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(100, 0),
                new Pt2d(100, 0.1), new Pt2d(0, 0.1)
            };
            Assert.True(Geo.PointInPolygon(new Pt2d(50, 0.05), poly));
            Assert.False(Geo.PointInPolygon(new Pt2d(50, 0.2), poly));
            Assert.False(Geo.PointInPolygon(new Pt2d(-1, 0.05), poly));
        }

        // --- Тест: полигон с «шипом» (spike) ---

        [Fact]
        public void PIP_SpikePolygon()
        {
            // Квадрат с шипом вверх
            var poly = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(10, 0), new Pt2d(10, 10),
                new Pt2d(6, 10), new Pt2d(5, 20), new Pt2d(4, 10),
                new Pt2d(0, 10)
            };
            // Внутри квадратной части
            Assert.True(Geo.PointInPolygon(new Pt2d(5, 5), poly));
            // Внутри шипа
            Assert.True(Geo.PointInPolygon(new Pt2d(5, 15), poly));
            // Рядом с шипом но снаружи
            Assert.False(Geo.PointInPolygon(new Pt2d(3, 15), poly));
            Assert.False(Geo.PointInPolygon(new Pt2d(7, 15), poly));
        }

        // --- CC: ячейка ровно совпадает с границей ---

        [Fact]
        public void CC_CellExactlyMatchesBoundary()
        {
            var boundary = new List<Pt2d>
            {
                new Pt2d(0, 0), new Pt2d(10, 0),
                new Pt2d(10, 10), new Pt2d(0, 10)
            };
            // Ячейка точно = границе
            // Углы ячейки на рёбрах/вершинах полигона — поведение зависит от ray-casting на ребре.
            // Главное: не Outside и не кидает исключение.
            var cls = Geo.ClassifyCell(CellCorners(0, 0, 10, 10), boundary);
            Assert.NotEqual(CellClass.Outside, cls);
        }

        // --- SI: множество пересечений за один проход ---

        [Fact]
        public void SI_MultipleIntersections_Grid()
        {
            // Горизонтальный отрезок пересекает несколько вертикальных
            var horizontal_a = new Pt2d(0, 5);
            var horizontal_b = new Pt2d(100, 5);
            int hits = 0;
            for (int x = 10; x < 100; x += 10)
            {
                if (Geo.SegmentIntersect(horizontal_a, horizontal_b,
                    new Pt2d(x, 0), new Pt2d(x, 10), out _, out _))
                    hits++;
            }
            Assert.Equal(9, hits);
        }

        // --- CC: полная сетка по U-фигуре ---

        [Fact]
        public void CC_FullGrid_UShape()
        {
            var boundary = UShape();
            int inside = 0, partial = 0, outside = 0;
            for (double x = 0; x < 10; x += 1)
            for (double y = 0; y < 10; y += 1)
            {
                var cls = Geo.ClassifyCell(CellCorners(x, y, 1, 1), boundary);
                switch (cls)
                {
                    case CellClass.Inside:  inside++;  break;
                    case CellClass.Partial: partial++;  break;
                    case CellClass.Outside: outside++; break;
                }
            }
            Assert.Equal(100, inside + partial + outside);
            Assert.True(inside > 0, "U-фигура должна иметь ячейки Inside");
            Assert.True(outside > 0, "Вырез U должен давать ячейки Outside");
        }

        // --- PIP: негативные координаты ---

        [Fact]
        public void PIP_NegativeCoordinates()
        {
            var poly = new List<Pt2d>
            {
                new Pt2d(-10, -10), new Pt2d(10, -10),
                new Pt2d(10, 10), new Pt2d(-10, 10)
            };
            Assert.True(Geo.PointInPolygon(new Pt2d(0, 0), poly));
            Assert.True(Geo.PointInPolygon(new Pt2d(-5, -5), poly));
            Assert.False(Geo.PointInPolygon(new Pt2d(-11, 0), poly));
        }

        // --- CC: «бублик» — граница-бублик не поддерживается PIP, но проверяем стабильность ---

        [Fact]
        public void CC_ManySmallCellsInsideBigBoundary()
        {
            var boundary = BigSquare(); // 100×100
            int total = 0, insideCount = 0;
            // 1000 маленьких ячеек
            for (double x = 10; x < 90; x += 4)
            for (double y = 10; y < 90; y += 4)
            {
                total++;
                var cls = Geo.ClassifyCell(CellCorners(x, y, 3, 3), boundary);
                if (cls == CellClass.Inside) insideCount++;
            }
            // Все ячейки полностью внутри (10..89 в 0..100)
            Assert.Equal(total, insideCount);
        }

        // --- Тест согласованности: PIP + ClassifyCell ---

        [Fact]
        public void Consistency_PIP_And_ClassifyCell_Triangle()
        {
            var boundary = Triangle(); // (0,0)-(10,0)-(5,10)
            int cellsWithCenterInside = 0;
            int classifiedInside = 0;

            for (double x = 0; x < 10; x += 2)
            for (double y = 0; y < 10; y += 2)
            {
                var center = new Pt2d(x + 1, y + 1);
                if (Geo.PointInPolygon(center, boundary))
                    cellsWithCenterInside++;

                var cls = Geo.ClassifyCell(CellCorners(x, y, 2, 2), boundary);
                if (cls == CellClass.Inside)
                    classifiedInside++;
            }

            // Inside ⊂ {cells with center inside}: каждая Inside-ячейка имеет центр внутри
            // (обратное не обязательно — Partial ячейка тоже может иметь центр внутри)
            Assert.True(classifiedInside <= cellsWithCenterInside,
                $"Inside ({classifiedInside}) не должно превышать cells-with-center-inside ({cellsWithCenterInside})");
        }
    }
}
