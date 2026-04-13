using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using RevitPlanningPlugin.Models.Domain;
using RevitPlanningPlugin.Models.Enums;

namespace RevitPlanningPlugin.Tests.Domain
{
    public class BuildingContourTests
    {
        // ——— Вспомогательный метод: прямоугольник 10×5 (CCW) ———
        private static BuildingContour MakeRect(double w = 10, double h = 5)
        {
            var pts = new[] {
                new Point2D(0, 0), new Point2D(w, 0),
                new Point2D(w, h), new Point2D(0, h)
            };
            return new BuildingContour
            {
                Id = "test",
                SourceUnit = "m",
                OuterLoop = BuildLoop(pts)
            };
        }

        private static List<ContourSegment> BuildLoop(Point2D[] pts)
        {
            var segs = new List<ContourSegment>();
            for (int i = 0; i < pts.Length; i++)
                segs.Add(new ContourSegment
                {
                    Type = SegmentType.Line,
                    Start = pts[i],
                    End = pts[(i + 1) % pts.Length]
                });
            return segs;
        }

        // ——— ApproximateArea ———

        [Fact]
        public void ApproximateArea_Rectangle_Correct()
        {
            var contour = MakeRect(10, 5);
            Assert.Equal(50.0, contour.ApproximateArea, precision: 6);
        }

        [Fact]
        public void ApproximateArea_EmptyOuterLoop_ReturnsZero()
        {
            var contour = new BuildingContour();
            Assert.Equal(0.0, contour.ApproximateArea);
        }

        [Fact]
        public void ApproximateArea_TwoPoints_ReturnsZero()
        {
            var contour = new BuildingContour
            {
                OuterLoop = new List<ContourSegment>
                {
                    new() { Start = new Point2D(0,0), End = new Point2D(1,0) },
                    new() { Start = new Point2D(1,0), End = new Point2D(0,0) }
                }
            };
            Assert.Equal(0.0, contour.ApproximateArea, precision: 10);
        }

        // ——— IsClosed ———

        [Fact]
        public void IsClosed_ClosedLoop_ReturnsTrue()
        {
            var contour = MakeRect();
            Assert.True(contour.IsClosed);
        }

        [Fact]
        public void IsClosed_OpenLoop_ReturnsFalse()
        {
            var contour = new BuildingContour
            {
                OuterLoop = new List<ContourSegment>
                {
                    new() { Start = new Point2D(0,0), End = new Point2D(1,0) },
                    new() { Start = new Point2D(1,0), End = new Point2D(1,1) }
                    // End (1,1) ≠ Start (0,0)
                }
            };
            Assert.False(contour.IsClosed);
        }

        [Fact]
        public void IsClosed_EmptyLoop_ReturnsFalse()
        {
            var contour = new BuildingContour();
            Assert.False(contour.IsClosed);
        }

        // ——— HasCurvedGeometry ———

        [Fact]
        public void HasCurvedGeometry_AllLines_ReturnsFalse()
        {
            var contour = MakeRect();
            Assert.False(contour.HasCurvedGeometry);
        }

        [Fact]
        public void HasCurvedGeometry_ContainsArc_ReturnsTrue()
        {
            var contour = MakeRect();
            contour.OuterLoop[0].Type = SegmentType.Arc;
            Assert.True(contour.HasCurvedGeometry);
        }

        [Fact]
        public void HasCurvedGeometry_InnerLoopHasArc_ReturnsTrue()
        {
            var contour = MakeRect();
            var inner = BuildLoop(new[] {
                new Point2D(2,1), new Point2D(4,1),
                new Point2D(4,3), new Point2D(2,3)
            });
            inner[0].Type = SegmentType.Arc;
            contour.InnerLoops.Add(inner);
            Assert.True(contour.HasCurvedGeometry);
        }

        // ——— GetOuterVertices ———

        [Fact]
        public void GetOuterVertices_ReturnsStartOfEachSegment()
        {
            var contour = MakeRect(10, 5);
            var verts = contour.GetOuterVertices();
            Assert.Equal(4, verts.Count);
            Assert.Equal(new Point2D(0, 0), verts[0]);
            Assert.Equal(new Point2D(10, 0), verts[1]);
            Assert.Equal(new Point2D(10, 5), verts[2]);
            Assert.Equal(new Point2D(0, 5), verts[3]);
        }

        // ——— ApproximateArea — криволинейные контуры ———

        /// <summary>
        /// Полукруг (дуга + одна хорда-диаметр): площадь должна быть близка к π*r²/2.
        /// Радиус 10 → ожидаемая площадь ≈ 157.08 м².
        /// Linearized version даёт лучшую оценку, чем только вершины.
        /// </summary>
        [Fact]
        public void ApproximateArea_SemicircleArc_CloserToActualThanLinear()
        {
            double r = 10;
            double expected = Math.PI * r * r / 2; // ≈ 157.08

            // Полукруг: дуга от (-r,0) до (r,0) через верхнюю полуокружность + хорда (диаметр)
            var outerLoop = new List<ContourSegment>
            {
                new() {
                    Type = SegmentType.Arc,
                    Start = new Point2D(-r, 0), End = new Point2D(r, 0),
                    ArcCenter = new Point2D(0, 0), ArcRadius = r, ArcClockwise = false
                },
                // Один диаметральный отрезок закрывает контур
                new() {
                    Type = SegmentType.Line,
                    Start = new Point2D(r, 0), End = new Point2D(-r, 0)
                }
            };
            var contour = new BuildingContour { OuterLoop = outerLoop };

            double linearArea  = contour.GetOuterVertices().Count >= 3
                ? ComputeShoelace(contour.GetOuterVertices())
                : 0;
            double curvedArea  = contour.ApproximateArea;

            // Curved estimate должна быть ближе к реальной площади, чем простая полигональная
            double errLinear = Math.Abs(linearArea - expected);
            double errCurved = Math.Abs(curvedArea - expected);
            Assert.True(errCurved < errLinear,
                $"Curved area ({curvedArea:F2}) should be closer to {expected:F2} than linear ({linearArea:F2})");
        }

        /// <summary>
        /// Прямоугольный контур без дуг — ApproximateArea и GetLinearizedVertices должны
        /// давать одинаковый результат (нет криволинейных сегментов).
        /// </summary>
        [Fact]
        public void ApproximateArea_RectNoArcs_EqualToLinearizedArea()
        {
            var contour = MakeRect(10, 5);
            // Без дуг linearized == polygon
            Assert.Equal(contour.ApproximateArea,
                         ComputeShoelace(contour.GetLinearizedVertices()),
                         precision: 6);
        }

        /// <summary>
        /// GetLinearizedVertices для контура с дугой возвращает больше точек,
        /// чем GetOuterVertices (только стартовые точки).
        /// </summary>
        [Fact]
        public void GetLinearizedVertices_ArcContour_HasMorePointsThanOuterVertices()
        {
            double r = 5;
            var outerLoop = new List<ContourSegment>
            {
                new() {
                    Type = SegmentType.Arc,
                    Start = new Point2D(-r, 0), End = new Point2D(r, 0),
                    ArcCenter = new Point2D(0, 0), ArcRadius = r, ArcClockwise = false
                },
                new() { Type = SegmentType.Line, Start = new Point2D(r, 0),  End = new Point2D(0, 0) },
                new() { Type = SegmentType.Line, Start = new Point2D(0, 0),  End = new Point2D(-r, 0) }
            };
            var contour = new BuildingContour { OuterLoop = outerLoop };

            int outerCount      = contour.GetOuterVertices().Count;
            int linearizedCount = contour.GetLinearizedVertices().Count;

            Assert.True(linearizedCount > outerCount,
                $"Linearized ({linearizedCount}) should have more points than outer vertices ({outerCount})");
        }

        // ——— Вспомогательный метод для вычисления площади по Шёлейсу ———

        private static double ComputeShoelace(List<Point2D> pts)
        {
            if (pts.Count < 3) return 0;
            double area = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var j = (i + 1) % pts.Count;
                area += pts[i].X * pts[j].Y;
                area -= pts[j].X * pts[i].Y;
            }
            return Math.Abs(area) / 2.0;
        }

        [Fact]
        public void AddGenerationResult_StoresVariants()
        {
            var contour = MakeRect();
            var variants = new List<LayoutVariant>
            {
                new() { Id = "v1" }, new() { Id = "v2" }
            };
            contour.AddGenerationResult(variants);
            Assert.Equal(2, contour.TotalGeneratedVariants);
        }

        [Fact]
        public void AddGenerationResult_MultipleTimes_HistoryGrows()
        {
            var contour = MakeRect();
            contour.AddGenerationResult(new List<LayoutVariant> { new() { Id = "v1" } });
            System.Threading.Thread.Sleep(10); // разные ключи DateTime
            contour.AddGenerationResult(new List<LayoutVariant> { new() { Id = "v2" }, new() { Id = "v3" } });
            Assert.Equal(3, contour.TotalGeneratedVariants);
        }

        [Fact]
        public void GetAllVariants_ReturnsAllAcrossRuns()
        {
            var contour = MakeRect();
            contour.AddGenerationResult(new List<LayoutVariant> { new() { Id = "a" } });
            System.Threading.Thread.Sleep(10);
            contour.AddGenerationResult(new List<LayoutVariant> { new() { Id = "b" }, new() { Id = "c" } });
            var all = contour.GetAllVariants();
            Assert.Equal(3, all.Count);
            Assert.Contains(all, v => v.Id == "a");
            Assert.Contains(all, v => v.Id == "c");
        }
    }
}
