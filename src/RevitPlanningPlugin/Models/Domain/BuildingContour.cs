using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitPlanningPlugin.Models.Domain
{
    /// <summary>
    /// Контур здания (внешний периметр + опциональные внутренние отверстия).
    /// Все координаты хранятся в метрах.
    /// </summary>
    public class BuildingContour
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>Внешний замкнутый контур.</summary>
        public List<ContourSegment> OuterLoop { get; set; } = new();

        /// <summary>Внутренние вырезы (дворы, шахты и т.п.).</summary>
        public List<List<ContourSegment>> InnerLoops { get; set; } = new();

        /// <summary>Метаданные из API.</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>Единица измерения координат, полученная из API.</summary>
        public string SourceUnit { get; set; } = "m";

        /// <summary>
        /// Оценка площади контура по формуле Шёлейса.
        /// Для криволинейных сегментов (Arc, Spline, Ellipse, NURBS) включает
        /// промежуточные точки линеаризации, что даёт лучшее приближение, чем
        /// использование только вершин полигона.
        /// </summary>
        public double ApproximateArea
        {
            get
            {
                var pts = HasCurvedGeometry ? GetLinearizedVertices() : GetOuterVertices();
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
        }

        /// <summary>
        /// Возвращает аппроксимированные вершины контура, включая промежуточные
        /// точки для криволинейных сегментов (8 сэмплов на кривую).
        /// </summary>
        public List<Point2D> GetLinearizedVertices(int samplesPerCurve = 8)
        {
            var vertices = new List<Point2D>();
            foreach (var seg in OuterLoop)
            {
                vertices.Add(seg.Start);
                if (seg.IsCurved)
                    vertices.AddRange(GetCurveMidpoints(seg, samplesPerCurve));
            }
            return vertices;
        }

        private static IEnumerable<Point2D> GetCurveMidpoints(ContourSegment seg, int samples)
        {
            switch (seg.Type)
            {
                case Models.Enums.SegmentType.Arc when seg.ArcCenter != null:
                {
                    double cx = seg.ArcCenter.X, cy = seg.ArcCenter.Y;
                    double radius = seg.Start.DistanceTo(seg.ArcCenter);
                    double a1 = Math.Atan2(seg.Start.Y - cy, seg.Start.X - cx);
                    double a2 = Math.Atan2(seg.End.Y - cy, seg.End.X - cx);
                    double sweep = a2 - a1;
                    if (seg.ArcClockwise) { if (sweep > 0) sweep -= 2 * Math.PI; }
                    else { if (sweep < 0) sweep += 2 * Math.PI; }

                    for (int i = 1; i < samples; i++)
                    {
                        double t = (double)i / samples;
                        double angle = a1 + sweep * t;
                        yield return new Point2D(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
                    }
                    break;
                }

                case Models.Enums.SegmentType.Spline:
                case Models.Enums.SegmentType.NurbsSpline:
                    if (seg.SplineControlPoints != null)
                        foreach (var cp in seg.SplineControlPoints)
                            yield return cp;
                    break;

                case Models.Enums.SegmentType.Ellipse
                    when seg.EllipseCenter != null && seg.EllipseRadiusX.HasValue && seg.EllipseRadiusY.HasValue:
                {
                    double cx = seg.EllipseCenter.X, cy = seg.EllipseCenter.Y;
                    double rx = seg.EllipseRadiusX.Value, ry = seg.EllipseRadiusY.Value;
                    double rot = seg.EllipseRotation;
                    double a1 = seg.EllipseStartAngle ?? 0;
                    double sweep = (seg.EllipseEndAngle ?? (2 * Math.PI)) - a1;

                    for (int i = 1; i < samples; i++)
                    {
                        double t = (double)i / samples;
                        double angle = a1 + sweep * t;
                        double ex = rx * Math.Cos(angle);
                        double ey = ry * Math.Sin(angle);
                        yield return new Point2D(
                            cx + ex * Math.Cos(rot) - ey * Math.Sin(rot),
                            cy + ex * Math.Sin(rot) + ey * Math.Cos(rot));
                    }
                    break;
                }
            }
        }

        public List<Point2D> GetOuterVertices()
        {
            var vertices = new List<Point2D>();
            foreach (var seg in OuterLoop)
            {
                vertices.Add(seg.Start);
            }
            return vertices;
        }

        /// <summary>Проверка замкнутости внешнего контура.</summary>
        public bool IsClosed
        {
            get
            {
                if (OuterLoop.Count == 0) return false;
                var first = OuterLoop.First().Start;
                var last = OuterLoop.Last().End;
                return first.Equals(last);
            }
        }

        /// <summary>Содержит ли контур криволинейные сегменты (неортогональный / органичный).</summary>
        public bool HasCurvedGeometry => OuterLoop.Any(s => s.IsCurved) 
            || InnerLoops.Any(loop => loop.Any(s => s.IsCurved));

        /// <summary>Типы кривых, используемые в контуре.</summary>
        public string GeometryDescription
        {
            get
            {
                var types = OuterLoop.Select(s => s.Type).Distinct().OrderBy(t => t);
                var desc = string.Join(", ", types);
                return HasCurvedGeometry ? $"Неортогональный ({desc})" : $"Ортогональный ({desc})";
            }
        }

        /// <summary>
        /// История генераций для этого контура.
        /// Ключ — timestamp, значение — список вариантов.
        /// Позволяет получить все сгенерированные планы по одному контуру.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public Dictionary<DateTime, List<LayoutVariant>> GenerationHistory { get; } = new();

        /// <summary>Общее количество сгенерированных вариантов по этому контуру.</summary>
        public int TotalGeneratedVariants => GenerationHistory.Values.Sum(v => v.Count);

        /// <summary>Добавить результат генерации в историю.</summary>
        public void AddGenerationResult(List<LayoutVariant> variants)
        {
            GenerationHistory[DateTime.Now] = variants;
        }

        /// <summary>Получить все варианты, когда-либо сгенерированные для этого контура.</summary>
        public List<LayoutVariant> GetAllVariants()
        {
            return GenerationHistory.Values.SelectMany(v => v).ToList();
        }
    }
}
