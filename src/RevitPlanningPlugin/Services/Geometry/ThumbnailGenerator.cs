using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RevitPlanningPlugin.Models.Domain;
using RevitPlanningPlugin.Models.Enums;

namespace RevitPlanningPlugin.Services.Geometry
{
    /// <summary>
    /// Генератор SVG-миниатюр для каталожного отображения вариантов планировки.
    /// Создаёт компактные векторные превью для быстрого переключения в галерее.
    /// </summary>
    public static class ThumbnailGenerator
    {
        private const int ThumbWidth = 200;
        private const int ThumbHeight = 160;
        private const int Padding = 10;

        // Цвета по типам помещений
        private static readonly Dictionary<RoomType, string> RoomColors = new()
        {
            [RoomType.LivingRoom] = "#E3F2FD",
            [RoomType.Bedroom] = "#F3E5F5",
            [RoomType.Kitchen] = "#FFF3E0",
            [RoomType.Bathroom] = "#E0F7FA",
            [RoomType.Corridor] = "#F5F5F5",
            [RoomType.Office] = "#E8F5E9",
            [RoomType.MeetingRoom] = "#FFF9C4",
            [RoomType.Storage] = "#EFEBE9",
            [RoomType.OpenSpace] = "#E1F5FE",
            [RoomType.Lobby] = "#FCE4EC",
            [RoomType.Technical] = "#ECEFF1",
            [RoomType.Staircase] = "#D7CCC8",
            [RoomType.Elevator] = "#CFD8DC",
            [RoomType.Balcony] = "#DCEDC8",
            [RoomType.Other] = "#EEEEEE"
        };

        /// <summary>
        /// Генерирует SVG-миниатюру варианта планировки.
        /// </summary>
        public static string GenerateSvg(LayoutVariant variant, BuildingContour? contour = null)
        {
            // Определяем bounding box
            var allPoints = new List<Point2D>();

            if (contour != null)
                allPoints.AddRange(contour.GetOuterVertices());

            foreach (var room in variant.Rooms)
                foreach (var seg in room.Boundary)
                {
                    allPoints.Add(seg.Start);
                    allPoints.Add(seg.End);
                }

            if (!allPoints.Any()) return string.Empty;

            double minX = allPoints.Min(p => p.X);
            double maxX = allPoints.Max(p => p.X);
            double minY = allPoints.Min(p => p.Y);
            double maxY = allPoints.Max(p => p.Y);

            double dataW = maxX - minX;
            double dataH = maxY - minY;
            if (dataW < 0.01 || dataH < 0.01) return string.Empty;

            // Scale to fit
            double drawW = ThumbWidth - 2 * Padding;
            double drawH = ThumbHeight - 2 * Padding;
            double scale = Math.Min(drawW / dataW, drawH / dataH);

            Func<Point2D, (double x, double y)> transform = (pt) =>
            {
                double x = Padding + (pt.X - minX) * scale;
                double y = Padding + (maxY - pt.Y) * scale; // flip Y
                return (x, y);
            };

            var sb = new StringBuilder();
            sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' width='{ThumbWidth}' height='{ThumbHeight}' viewBox='0 0 {ThumbWidth} {ThumbHeight}'>");
            sb.AppendLine($"<rect width='{ThumbWidth}' height='{ThumbHeight}' fill='white' rx='4'/>");

            // Контур здания
            if (contour != null)
            {
                var contourPath = BuildPolygonPath(contour.GetOuterVertices(), transform);
                sb.AppendLine($"<path d='{contourPath}' fill='none' stroke='#333' stroke-width='2'/>");
            }

            // Помещения
            foreach (var room in variant.Rooms)
            {
                var vertices = room.Boundary.Select(s => s.Start).ToList();
                if (vertices.Count < 3) continue;

                var color = RoomColors.TryGetValue(room.Type, out var c) ? c : "#EEEEEE";
                var path = BuildPolygonPath(vertices, transform);
                sb.AppendLine($"<path d='{path}' fill='{color}' stroke='#999' stroke-width='0.5' opacity='0.8'/>");

                // Метка помещения (если вписывается)
                if (room.LabelPoint != null)
                {
                    var (lx, ly) = transform(room.LabelPoint);
                    var shortName = room.Name.Length > 6 ? room.Name.Substring(0, 6) : room.Name;
                    sb.AppendLine($"<text x='{F(lx)}' y='{F(ly)}' font-size='6' fill='#666' text-anchor='middle' dominant-baseline='middle'>{HtmlEncode(shortName)}</text>");
                }
            }

            // Перегородки
            foreach (var part in variant.Partitions)
            {
                var (sx, sy) = transform(part.Start);
                var (ex, ey) = transform(part.End);
                sb.AppendLine($"<line x1='{F(sx)}' y1='{F(sy)}' x2='{F(ex)}' y2='{F(ey)}' stroke='#555' stroke-width='1'/>");
            }

            // Score badge
            sb.AppendLine($"<rect x='{ThumbWidth - 45}' y='2' width='43' height='16' rx='3' fill='#1565C0'/>");
            sb.AppendLine($"<text x='{ThumbWidth - 24}' y='13' font-size='9' fill='white' text-anchor='middle' font-weight='bold'>{variant.EfficiencyScore:F0}</text>");

            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        /// <summary>
        /// Генерирует миниатюры для всех вариантов пакетно.
        /// </summary>
        public static void GenerateThumbnails(List<LayoutVariant> variants, BuildingContour? contour = null)
        {
            foreach (var variant in variants)
            {
                variant.ThumbnailSvg = GenerateSvg(variant, contour);
            }
        }

        private static string BuildPolygonPath(List<Point2D> vertices, Func<Point2D, (double x, double y)> transform)
        {
            if (vertices.Count < 2) return string.Empty;

            var sb = new StringBuilder();
            var (fx, fy) = transform(vertices[0]);
            sb.Append($"M{F(fx)},{F(fy)}");

            for (int i = 1; i < vertices.Count; i++)
            {
                var (px, py) = transform(vertices[i]);
                sb.Append($" L{F(px)},{F(py)}");
            }
            sb.Append(" Z");
            return sb.ToString();
        }

        private static string F(double val) => val.ToString("F1", CultureInfo.InvariantCulture);

        /// <summary>Экранирует спецсимволы XML/SVG в строке.</summary>
        private static string HtmlEncode(string text)
            => text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }
}
