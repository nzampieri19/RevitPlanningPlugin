using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitPlanningPlugin.Models.Domain;
using RevitPlanningPlugin.Revit.Geometry;
using RevitPlanningPlugin.Revit.Transactions;
using RevitPlanningPlugin.Services.Logging;

namespace RevitPlanningPlugin.Revit.Elements
{
    /// <summary>
    /// Хранилище созданных элементов Revit для управления жизненным циклом.
    /// </summary>
    public class CreatedElementsTracker
    {
        private readonly List<ElementId> _elementIds = new();

        public IReadOnlyList<ElementId> ElementIds => _elementIds;

        public void Track(ElementId id)
        {
            if (id != null && id != ElementId.InvalidElementId)
                _elementIds.Add(id);
        }

        public void TrackRange(IEnumerable<ElementId> ids)
        {
            foreach (var id in ids)
                Track(id);
        }

        /// <summary>
        /// Удаляет все отслеживаемые элементы из документа.
        /// </summary>
        public int DeleteAll(Document doc)
        {
            if (_elementIds.Count == 0) return 0;

            var toDelete = _elementIds
                .Where(id => doc.GetElement(id) != null)
                .ToList();

            if (toDelete.Count == 0) return 0;

            var collection = new List<ElementId>(toDelete);
            doc.Delete(collection);

            var count = toDelete.Count;
            _elementIds.Clear();
            PluginLogger.Info($"Удалено {count} элементов.");
            return count;
        }
    }

    /// <summary>
    /// Создание элементов Revit из доменных моделей контура и планировки.
    /// </summary>
    public class RevitElementCreator
    {
        private const string ContourLineStyleName = "PlanningPlugin_Contour";
        private const string PartitionLineStyleName = "PlanningPlugin_Partition";

        // Трекер для предпросмотра (удаляется при переключении варианта)
        public CreatedElementsTracker PreviewTracker { get; } = new();

        // Трекер для окончательно примененного варианта
        public CreatedElementsTracker AppliedTracker { get; } = new();

        /// <summary>
        /// Отрисовывает контур здания в Revit (Detail Lines на активном виде).
        /// </summary>
        public void DrawContour(Document doc, View activeView, BuildingContour contour, Level level)
        {
            SafeTransaction.Execute(doc, "Отрисовка контура", tx =>
            {
                var curves = RevitCurveBuilder.BuildCurves(contour.OuterLoop,
                    level.Elevation * Services.Geometry.UnitConverter.FeetToMeters);

                foreach (var curve in curves)
                {
                    var line = doc.Create.NewDetailCurve(activeView, curve);
                    PreviewTracker.Track(line.Id);
                }

                // Внутренние контуры
                foreach (var innerLoop in contour.InnerLoops)
                {
                    var innerCurves = RevitCurveBuilder.BuildCurves(innerLoop,
                        level.Elevation * Services.Geometry.UnitConverter.FeetToMeters);
                    foreach (var curve in innerCurves)
                    {
                        var line = doc.Create.NewDetailCurve(activeView, curve);
                        PreviewTracker.Track(line.Id);
                    }
                }

                PluginLogger.Info($"Контур '{contour.Name}' отрисован: {curves.Count} кривых.");
            });
        }

        /// <summary>
        /// Отрисовывает предпросмотр варианта планировки (перегородки как Detail Lines).
        /// Предыдущий предпросмотр удаляется.
        /// </summary>
        public void DrawLayoutPreview(Document doc, View activeView, LayoutVariant variant, Level level)
        {
            SafeTransaction.Execute(doc, "Предпросмотр планировки", tx =>
            {
                // Удаляем предыдущий предпросмотр
                PreviewTracker.DeleteAll(doc);

                double elev = level.Elevation * Services.Geometry.UnitConverter.FeetToMeters;

                // Рисуем перегородки
                foreach (var partition in variant.Partitions)
                {
                    var curve = RevitCurveBuilder.BuildCurve(partition, elev);
                    if (curve != null)
                    {
                        var line = doc.Create.NewDetailCurve(activeView, curve);
                        PreviewTracker.Track(line.Id);
                    }
                }

                // Рисуем границы помещений
                foreach (var room in variant.Rooms)
                {
                    var roomCurves = RevitCurveBuilder.BuildCurves(room.Boundary, elev);
                    foreach (var curve in roomCurves)
                    {
                        var line = doc.Create.NewDetailCurve(activeView, curve);
                        PreviewTracker.Track(line.Id);
                    }
                }

                PluginLogger.Info($"Предпросмотр варианта '{variant.Name}': " +
                    $"{variant.Partitions.Count} перегородок, {variant.Rooms.Count} помещений.");
            });
        }

        /// <summary>
        /// Применяет вариант в модель: создаёт Room Separation Lines + Room элементы.
        /// </summary>
        public void ApplyLayout(Document doc, LayoutVariant variant, Level level)
        {
            SafeTransaction.ExecuteGroup(doc, $"Применение варианта '{variant.Name}'", () =>
            {
                // 1. Удаляем ранее применённый вариант (если есть)
                SafeTransaction.Execute(doc, "Очистка предыдущего варианта", tx =>
                {
                    AppliedTracker.DeleteAll(doc);
                    PreviewTracker.DeleteAll(doc);
                });

                // 2. Создаём Room Separation Lines для перегородок
                SafeTransaction.Execute(doc, "Создание разделителей помещений", tx =>
                {
                    CreateRoomSeparators(doc, variant, level);
                });

                // 3. Создаём Room-элементы
                SafeTransaction.Execute(doc, "Создание помещений", tx =>
                {
                    CreateRooms(doc, variant, level);
                });

                PluginLogger.Info($"Вариант '{variant.Name}' применён в модель.");
            });
        }

        /// <summary>
        /// Применяет вариант со стенами (опционально).
        /// </summary>
        public void ApplyLayoutWithWalls(Document doc, LayoutVariant variant, Level level, WallType wallType, double wallHeight = 3.0)
        {
            SafeTransaction.ExecuteGroup(doc, $"Применение варианта со стенами '{variant.Name}'", () =>
            {
                SafeTransaction.Execute(doc, "Очистка", tx =>
                {
                    AppliedTracker.DeleteAll(doc);
                    PreviewTracker.DeleteAll(doc);
                });

                SafeTransaction.Execute(doc, "Создание стен", tx =>
                {
                    CreateWalls(doc, variant, level, wallType, wallHeight);
                });

                SafeTransaction.Execute(doc, "Создание помещений", tx =>
                {
                    CreateRooms(doc, variant, level);
                });
            });
        }

        // ——— Приватные методы ———

        private void CreateRoomSeparators(Document doc, LayoutVariant variant, Level level)
        {
            var sketchPlane = GetSketchPlane(doc, level);
            AppliedTracker.Track(sketchPlane.Id);

            // Level.Elevation is in Revit internal units (feet); convert to meters
            // for RevitCurveBuilder which expects meters and converts back to feet internally.
            double elevMeters = level.Elevation * Services.Geometry.UnitConverter.FeetToMeters;

            foreach (var partition in variant.Partitions)
            {
                var curve = RevitCurveBuilder.BuildCurve(partition, elevMeters);
                if (curve == null) continue;

                var curveArray = new CurveArray();
                curveArray.Append(curve);

                var sepLines = doc.Create.NewRoomBoundaryLines(
                    sketchPlane, curveArray, doc.ActiveView);

                if (sepLines != null)
                {
                    foreach (ModelCurve mc in sepLines)
                        AppliedTracker.Track(mc.Id);
                }
            }

            // Границы каждого помещения тоже как separation lines
            foreach (var room in variant.Rooms)
            {
                foreach (var seg in room.Boundary)
                {
                    var curve = RevitCurveBuilder.BuildCurve(seg, elevMeters);
                    if (curve == null) continue;

                    var ca = new CurveArray();
                    ca.Append(curve);

                    var sepLines = doc.Create.NewRoomBoundaryLines(sketchPlane, ca, doc.ActiveView);
                    if (sepLines != null)
                    {
                        foreach (ModelCurve mc in sepLines)
                            AppliedTracker.Track(mc.Id);
                    }
                }
            }
        }

        private void CreateRooms(Document doc, LayoutVariant variant, Level level)
        {
            // Level.Elevation (feet) → meters for ToXYZ which converts back to feet internally
            double elevMeters = level.Elevation * Services.Geometry.UnitConverter.FeetToMeters;

            foreach (var roomLayout in variant.Rooms)
            {
                if (roomLayout.LabelPoint == null) continue;

                var pt = RevitCurveBuilder.ToXYZ(roomLayout.LabelPoint, elevMeters);
                var uv = new UV(pt.X, pt.Y);

                try
                {
                    var room = doc.Create.NewRoom(level, uv);
                    if (room != null)
                    {
                        room.Name = roomLayout.Name;
                        AppliedTracker.Track(room.Id);
                    }
                }
                catch (Exception ex)
                {
                    PluginLogger.Warn($"Не удалось создать помещение '{roomLayout.Name}': {ex.Message}");
                }
            }
        }

        private void CreateWalls(Document doc, LayoutVariant variant, Level level,
            WallType wallType, double wallHeight)
        {
            double heightFeet = wallHeight * Services.Geometry.UnitConverter.MetersToFeet;
            double elevMeters = level.Elevation * Services.Geometry.UnitConverter.FeetToMeters;

            foreach (var partition in variant.Partitions)
            {
                var curve = RevitCurveBuilder.BuildCurve(partition, elevMeters);
                if (curve == null) continue;

                try
                {
                    var wall = Wall.Create(doc, curve, wallType.Id, level.Id, heightFeet, 0, false, false);
                    if (wall != null)
                        AppliedTracker.Track(wall.Id);
                }
                catch (Exception ex)
                {
                    PluginLogger.Warn($"Не удалось создать стену: {ex.Message}");
                }
            }
        }

        private static SketchPlane GetSketchPlane(Document doc, Level level)
        {
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ,
                new XYZ(0, 0, level.Elevation));
            return SketchPlane.Create(doc, plane);
        }
    }
}
