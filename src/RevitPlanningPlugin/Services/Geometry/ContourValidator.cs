using System;
using System.Collections.Generic;
using System.Linq;
using RevitPlanningPlugin.Models.Domain;
using RevitPlanningPlugin.Models.Enums;

namespace RevitPlanningPlugin.Services.Geometry
{
    /// <summary>
    /// Валидация геометрии контура: замкнутость, самопересечения,
    /// площадь, ориентация.
    /// Для криволинейных сегментов (Arc, Spline, Ellipse, NURBS) используется
    /// линеаризация (аппроксимация ломаной) для проверки самопересечений.
    /// </summary>
    public class ContourValidator
    {
        private const double ClosureTolerance = 0.001;   // 1 мм
        private const double MinAreaSqM = 1.0;            // мин. площадь 1 м²
        private const double MaxAreaSqM = 1_000_000.0;    // макс. площадь
        private const int CurveLinearizationSamples = 16; // точек на кривую

        public ValidationResult Validate(BuildingContour contour)
        {
            var result = new ValidationResult();

            if (contour.OuterLoop == null || contour.OuterLoop.Count < 3)
            {
                result.AddError("Внешний контур должен содержать не менее 3 сегментов.", "MIN_SEGMENTS");
                return result;
            }

            ValidateClosure(contour, result);
            ValidateSelfIntersections(contour, result);
            ValidateArea(contour, result);
            ValidateOrientation(contour, result);

            // Валидация внутренних контуров
            for (int i = 0; i < contour.InnerLoops.Count; i++)
            {
                var inner = contour.InnerLoops[i];
                if (inner.Count < 3)
                    result.AddWarning($"Внутренний контур #{i + 1}: менее 3 сегментов.", "INNER_MIN_SEGMENTS");
            }

            return result;
        }

        private void ValidateClosure(BuildingContour contour, ValidationResult result)
        {
            var first = contour.OuterLoop.First().Start;
            var last = contour.OuterLoop.Last().End;
            var gap = first.DistanceTo(last);

            if (gap > ClosureTolerance)
            {
                result.AddError(
                    $"Контур не замкнут: зазор {gap:F4} м между началом и концом.",
                    "NOT_CLOSED");
            }

            // Проверяем непрерывность: End[i] == Start[i+1]
            for (int i = 0; i < contour.OuterLoop.Count - 1; i++)
            {
                var endPt = contour.OuterLoop[i].End;
                var nextStart = contour.OuterLoop[i + 1].Start;
                if (endPt.DistanceTo(nextStart) > ClosureTolerance)
                {
                    result.AddError(
                        $"Разрыв между сегментами #{i} и #{i + 1}: {endPt.DistanceTo(nextStart):F4} м.",
                        "DISCONTINUITY");
                }
            }
        }

        private void ValidateSelfIntersections(BuildingContour contour, ValidationResult result)
        {
            // Linearize all segments into polyline sub-segments and check all non-adjacent pairs.
            var polyline = BuildLinearizedPolyline(contour.OuterLoop);
            int n = polyline.Count;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 2; j < n; j++)
                {
                    // Skip last segment paired with first (they share the closure vertex)
                    if (i == 0 && j == n - 1) continue;

                    if (SegmentsIntersect(
                        polyline[i].Item1, polyline[i].Item2,
                        polyline[j].Item1, polyline[j].Item2))
                    {
                        result.AddError(
                            $"Самопересечение контура (сегменты #{i} и #{j}).",
                            "SELF_INTERSECTION");
                        return; // достаточно одного сообщения
                    }
                }
            }
        }

        private void ValidateArea(BuildingContour contour, ValidationResult result)
        {
            var area = contour.ApproximateArea;
            if (area < MinAreaSqM)
                result.AddError($"Площадь контура слишком мала: {area:F2} м².", "AREA_TOO_SMALL");
            else if (area > MaxAreaSqM)
                result.AddWarning($"Площадь контура очень велика: {area:F0} м². Проверьте единицы измерения.", "AREA_TOO_LARGE");
        }

        private void ValidateOrientation(BuildingContour contour, ValidationResult result)
        {
            // Формула Шёлейса: положительная площадь → CCW (правильная ориентация),
            // отрицательная площадь → CW (предупреждение).
            var signedArea = ComputeSignedArea(contour.GetOuterVertices());
            if (signedArea < 0)
                result.AddWarning("Контур имеет обход по часовой стрелке. Рекомендуется против часовой.", "CW_ORIENTATION");
        }

        // ——— Linearization ———

        /// <summary>
        /// Преобразует список сегментов в список полилинейных пар точек.
        /// Прямые сегменты → 1 пара; кривые → N пар.
        /// </summary>
        private static List<(Point2D, Point2D)> BuildLinearizedPolyline(List<ContourSegment> segments)
        {
            var result = new List<(Point2D, Point2D)>();
            foreach (var seg in segments)
            {
                var pts = LinearizeSegment(seg);
                for (int i = 0; i < pts.Count - 1; i++)
                    result.Add((pts[i], pts[i + 1]));
            }
            return result;
        }

        /// <summary>
        /// Аппроксимирует сегмент набором точек:
        /// - Line:    [Start, End]
        /// - Arc:     N+1 точек вдоль дуги (сэмплинг по углу)
        /// - Spline:  Start + ControlPoints + End
        /// - Ellipse: N+1 точек вдоль дуги эллипса (сэмплинг по углу)
        /// - NURBS:   Start + ControlPoints + End
        /// </summary>
        private static List<Point2D> LinearizeSegment(ContourSegment seg)
        {
            switch (seg.Type)
            {
                case SegmentType.Arc:
                    return LinearizeArc(seg);

                case SegmentType.Spline:
                case SegmentType.NurbsSpline:
                    return LinearizeSpline(seg);

                case SegmentType.Ellipse:
                    return LinearizeEllipse(seg);

                default:
                    return new List<Point2D> { seg.Start, seg.End };
            }
        }

        private static List<Point2D> LinearizeArc(ContourSegment seg)
        {
            if (seg.ArcCenter == null)
                return new List<Point2D> { seg.Start, seg.End };

            double cx = seg.ArcCenter.X;
            double cy = seg.ArcCenter.Y;
            double dx1 = seg.Start.X - cx;
            double dy1 = seg.Start.Y - cy;
            double dx2 = seg.End.X - cx;
            double dy2 = seg.End.Y - cy;
            double a1 = Math.Atan2(dy1, dx1);
            double a2 = Math.Atan2(dy2, dx2);
            double radius = Math.Sqrt(dx1 * dx1 + dy1 * dy1);

            double sweep = a2 - a1;
            if (seg.ArcClockwise)
            {
                if (sweep > 0) sweep -= 2 * Math.PI;
            }
            else
            {
                if (sweep < 0) sweep += 2 * Math.PI;
            }

            var pts = new List<Point2D>();
            for (int i = 0; i <= CurveLinearizationSamples; i++)
            {
                double t = (double)i / CurveLinearizationSamples;
                double angle = a1 + sweep * t;
                pts.Add(new Point2D(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
            return pts;
        }

        private static List<Point2D> LinearizeSpline(ContourSegment seg)
        {
            var pts = new List<Point2D> { seg.Start };
            if (seg.SplineControlPoints != null)
                pts.AddRange(seg.SplineControlPoints);
            pts.Add(seg.End);
            return pts;
        }

        private static List<Point2D> LinearizeEllipse(ContourSegment seg)
        {
            if (!seg.EllipseRadiusX.HasValue || !seg.EllipseRadiusY.HasValue)
                return new List<Point2D> { seg.Start, seg.End };

            double cx = seg.EllipseCenter?.X ?? 0;
            double cy = seg.EllipseCenter?.Y ?? 0;
            double rx = seg.EllipseRadiusX.Value;
            double ry = seg.EllipseRadiusY.Value;
            double rot = seg.EllipseRotation;
            double a1 = seg.EllipseStartAngle ?? 0;
            double a2 = seg.EllipseEndAngle ?? (2 * Math.PI);
            double sweep = a2 - a1;

            var pts = new List<Point2D>();
            for (int i = 0; i <= CurveLinearizationSamples; i++)
            {
                double t = (double)i / CurveLinearizationSamples;
                double angle = a1 + sweep * t;
                double ex = rx * Math.Cos(angle);
                double ey = ry * Math.Sin(angle);
                // Apply rotation
                double x = cx + ex * Math.Cos(rot) - ey * Math.Sin(rot);
                double y = cy + ex * Math.Sin(rot) + ey * Math.Cos(rot);
                pts.Add(new Point2D(x, y));
            }
            return pts;
        }

        // ——— Утилиты ———

        private static double ComputeSignedArea(List<Point2D> pts)
        {
            double area = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var j = (i + 1) % pts.Count;
                area += pts[i].X * pts[j].Y;
                area -= pts[j].X * pts[i].Y;
            }
            return area / 2.0;
        }

        /// <summary>Проверка пересечения двух отрезков (2D).</summary>
        private static bool SegmentsIntersect(Point2D a1, Point2D a2, Point2D b1, Point2D b2)
        {
            double d1 = Cross(b1, b2, a1);
            double d2 = Cross(b1, b2, a2);
            double d3 = Cross(a1, a2, b1);
            double d4 = Cross(a1, a2, b2);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;

            return false;
        }

        private static double Cross(Point2D o, Point2D a, Point2D b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
