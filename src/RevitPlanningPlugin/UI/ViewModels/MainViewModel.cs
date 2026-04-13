using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitPlanningPlugin.Infrastructure;
using RevitPlanningPlugin.Models.Api;
using RevitPlanningPlugin.Models.Domain;
using RevitPlanningPlugin.Models.Enums;
using RevitPlanningPlugin.Revit.Elements;
using RevitPlanningPlugin.Services.Api;
using RevitPlanningPlugin.Services.Configuration;
using RevitPlanningPlugin.Services.Geometry;
using RevitPlanningPlugin.Services.Logging;

namespace RevitPlanningPlugin.UI.ViewModels
{
    /// <summary>
    /// Главная ViewModel плагина.
    /// Оркестрирует полный бесшовный цикл:
    /// подключение → контур → генерация → каталог вариантов → применение.
    /// Пользователь работает исключительно внутри Revit, без экспорта/импорта.
    /// </summary>
    public class MainViewModel : ObservableObject, IDisposable
    {
        // ——— Зависимости ———
        private readonly ExternalCommandData _commandData;
        private readonly ConfigurationService _configService;
        private IPlanningApiClient? _apiClient;
        private readonly ContourValidator _validator = new();
        private readonly RevitElementCreator _elementCreator = new();

        // ——— Состояние ———
        private GenerationStatus _status = GenerationStatus.Idle;
        private string _statusMessage = "Готов к работе";
        private BuildingContour? _currentContour;
        private LayoutVariant? _selectedVariant;
        private CancellationTokenSource? _cts;
        private int _generationProgress;
        private string _generationElapsed = string.Empty;

        public MainViewModel(ExternalCommandData commandData)
        {
            _commandData = commandData;
            _configService = new ConfigurationService();
            Settings = _configService.Load();

            InitializeApiClient();
            InitializeCommands();
        }

        // ═══════════════════════════════════════════
        //  Свойства: состояние, статус, прогресс
        // ═══════════════════════════════════════════

        public PluginSettings Settings { get; private set; }

        public GenerationStatus Status
        {
            get => _status;
            set
            {
                SetProperty(ref _status, value);
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsGenerating));
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsIdle => Status == GenerationStatus.Idle || Status == GenerationStatus.Completed;
        public bool IsBusy => !IsIdle;
        public bool IsGenerating => Status == GenerationStatus.Generating;

        /// <summary>Прогресс генерации (0–100).</summary>
        public int GenerationProgress
        {
            get => _generationProgress;
            set => SetProperty(ref _generationProgress, value);
        }

        /// <summary>Затраченное время на генерацию.</summary>
        public string GenerationElapsed
        {
            get => _generationElapsed;
            set => SetProperty(ref _generationElapsed, value);
        }

        // ═══════════════════════════════════════════
        //  Свойства: контуры
        // ═══════════════════════════════════════════

        public ObservableCollection<ApiContourSummaryDto> AvailableContours { get; } = new();

        private ApiContourSummaryDto? _selectedContourSummary;
        public ApiContourSummaryDto? SelectedContourSummary
        {
            get => _selectedContourSummary;
            set
            {
                SetProperty(ref _selectedContourSummary, value);
                OnPropertyChanged(nameof(CanLoadContour));
            }
        }

        public BuildingContour? CurrentContour
        {
            get => _currentContour;
            set
            {
                SetProperty(ref _currentContour, value);
                OnPropertyChanged(nameof(HasContour));
                OnPropertyChanged(nameof(ContourInfo));
                OnPropertyChanged(nameof(ContourGeometryInfo));
                OnPropertyChanged(nameof(GenerationHistoryInfo));
            }
        }

        public bool HasContour => _currentContour != null;
        public bool CanLoadContour => SelectedContourSummary != null && IsIdle;

        public string ContourInfo => _currentContour != null
            ? $"{_currentContour.Name} | {_currentContour.ApproximateArea:F1} м² | " +
              $"{_currentContour.OuterLoop.Count} сегментов"
            : "Контур не загружен";

        /// <summary>Описание геометрии: ортогональный / неортогональный / органичный.</summary>
        public string ContourGeometryInfo => _currentContour != null
            ? _currentContour.GeometryDescription
            : string.Empty;

        /// <summary>Инфо об истории генераций для текущего контура.</summary>
        public string GenerationHistoryInfo => _currentContour != null && _currentContour.TotalGeneratedVariants > 0
            ? $"Всего сгенерировано: {_currentContour.TotalGeneratedVariants} вариантов за {_currentContour.GenerationHistory.Count} запуск(ов)"
            : string.Empty;

        // ═══════════════════════════════════════════
        //  Свойства: параметры генерации
        // ═══════════════════════════════════════════

        public GenerationParameters GenerationParams { get; set; } = new();

        // ═══════════════════════════════════════════
        //  Свойства: варианты (каталожный режим)
        // ═══════════════════════════════════════════

        /// <summary>Текущий набор вариантов (последняя генерация).</summary>
        public ObservableCollection<LayoutVariant> Variants { get; } = new();

        /// <summary>Все когда-либо сгенерированные варианты для текущего контура.</summary>
        public ObservableCollection<LayoutVariant> AllVariants { get; } = new();

        /// <summary>Показывать все варианты из истории (или только последнюю генерацию).</summary>
        private bool _showAllHistory;
        public bool ShowAllHistory
        {
            get => _showAllHistory;
            set
            {
                SetProperty(ref _showAllHistory, value);
                OnPropertyChanged(nameof(DisplayedVariants));
            }
        }

        /// <summary>Коллекция для отображения в галерее.</summary>
        public ObservableCollection<LayoutVariant> DisplayedVariants
            => ShowAllHistory ? AllVariants : Variants;

        public LayoutVariant? SelectedVariant
        {
            get => _selectedVariant;
            set
            {
                if (SetProperty(ref _selectedVariant, value) && value != null)
                    PreviewVariant(value);
                OnPropertyChanged(nameof(HasSelectedVariant));
                OnPropertyChanged(nameof(VariantInfo));
                OnPropertyChanged(nameof(VariantApartmentTypeSummary));
                OnPropertyChanged(nameof(HasApartmentTypeInfo));
                OnPropertyChanged(nameof(SelectedVariantIndex));
                OnPropertyChanged(nameof(VariantNavigationInfo));
            }
        }

        public bool HasSelectedVariant => _selectedVariant != null;

        /// <summary>Индекс выбранного варианта (1-based) для каталожной навигации.</summary>
        public int SelectedVariantIndex
        {
            get
            {
                if (_selectedVariant == null) return 0;
                var list = DisplayedVariants;
                var idx = list.IndexOf(_selectedVariant);
                return idx >= 0 ? idx + 1 : 0;
            }
        }

        /// <summary>Навигационная строка «3 / 10».</summary>
        public string VariantNavigationInfo
        {
            get
            {
                var list = DisplayedVariants;
                if (list.Count == 0) return string.Empty;
                return $"{SelectedVariantIndex} / {list.Count}";
            }
        }

        public string VariantInfo => _selectedVariant != null
            ? _selectedVariant.MetricsDetail
            : string.Empty;

        public string VariantApartmentTypeSummary => _selectedVariant?.ApartmentTypeSummary ?? string.Empty;

        public bool HasApartmentTypeInfo =>
            _selectedVariant != null && _selectedVariant.ApartmentTypeDistribution.Count > 0;

        // ═══════════════════════════════════════════
        //  Валидация
        // ═══════════════════════════════════════════

        private ValidationResult? _validationResult;
        public ValidationResult? ValidationResult
        {
            get => _validationResult;
            set
            {
                SetProperty(ref _validationResult, value);
                OnPropertyChanged(nameof(ValidationMessages));
            }
        }

        public string ValidationMessages => _validationResult != null
            ? string.Join("\n", _validationResult.Issues.Select(i => $"[{i.Severity}] {i.Message}"))
            : string.Empty;

        // ═══════════════════════════════════════════
        //  Команды
        // ═══════════════════════════════════════════

        public ICommand TestConnectionCommand { get; private set; } = null!;
        public ICommand LoadContoursCommand { get; private set; } = null!;
        public ICommand LoadSelectedContourCommand { get; private set; } = null!;
        public ICommand GenerateCommand { get; private set; } = null!;
        public ICommand ApplyVariantCommand { get; private set; } = null!;
        public ICommand ApplyWithWallsCommand { get; private set; } = null!;
        public ICommand SaveSettingsCommand { get; private set; } = null!;
        public ICommand CancelCommand { get; private set; } = null!;

        // Каталожная навигация
        public ICommand NextVariantCommand { get; private set; } = null!;
        public ICommand PreviousVariantCommand { get; private set; } = null!;
        public ICommand ToggleHistoryCommand { get; private set; } = null!;

        private void InitializeCommands()
        {
            TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => IsIdle);
            LoadContoursCommand = new AsyncRelayCommand(LoadContoursAsync, () => IsIdle);
            LoadSelectedContourCommand = new AsyncRelayCommand(LoadSelectedContourAsync, () => CanLoadContour);
            GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => HasContour && IsIdle);
            ApplyVariantCommand = new RelayCommand(ApplySelectedVariant, () => HasSelectedVariant && IsIdle);
            ApplyWithWallsCommand = new RelayCommand(ApplySelectedVariantWithWalls, () => HasSelectedVariant && IsIdle);
            SaveSettingsCommand = new RelayCommand(SaveSettings);
            CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);

            // Быстрый переключатель ← →
            NextVariantCommand = new RelayCommand(NavigateNext, () => HasSelectedVariant);
            PreviousVariantCommand = new RelayCommand(NavigatePrevious, () => HasSelectedVariant);
            ToggleHistoryCommand = new RelayCommand(() => ShowAllHistory = !ShowAllHistory);
        }

        // ═══════════════════════════════════════════
        //  Каталожная навигация
        // ═══════════════════════════════════════════

        private void NavigateNext()
        {
            var list = DisplayedVariants;
            if (list.Count == 0) return;
            var idx = list.IndexOf(_selectedVariant);
            if (idx < list.Count - 1)
                SelectedVariant = list[idx + 1];
            else
                SelectedVariant = list[0]; // цикличный переход
        }

        private void NavigatePrevious()
        {
            var list = DisplayedVariants;
            if (list.Count == 0) return;
            var idx = list.IndexOf(_selectedVariant);
            if (idx > 0)
                SelectedVariant = list[idx - 1];
            else
                SelectedVariant = list[list.Count - 1]; // цикличный переход
        }

        // ═══════════════════════════════════════════
        //  Методы: API и подключение
        // ═══════════════════════════════════════════

        private void InitializeApiClient()
        {
            (_apiClient as IDisposable)?.Dispose();

            if (Settings.UseMockApi)
            {
                _apiClient = new MockPlanningApiClient();
                PluginLogger.Info("API-клиент: режим Mock (без реального API).");
            }
            else
            {
                _apiClient = new PlanningApiClient(Settings);
                PluginLogger.Info($"API-клиент: реальный API ({Settings.EffectiveBaseUrl}).");
            }
        }

        private async Task TestConnectionAsync()
        {
            try
            {
                SetStatus(GenerationStatus.Loading, "Проверка соединения…");
                var ok = await _apiClient!.TestConnectionAsync();
                SetStatus(GenerationStatus.Idle,
                    ok ? "Соединение установлено ✓" : "Не удалось подключиться к API ✗");
            }
            catch (Exception ex)
            {
                SetStatus(GenerationStatus.Error, $"Ошибка: {ex.Message}");
            }
        }

        private async Task LoadContoursAsync()
        {
            try
            {
                SetStatus(GenerationStatus.Loading, "Загрузка списка контуров…");
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                var contours = await _apiClient!.GetContourListAsync(_cts.Token);

                AvailableContours.Clear();
                foreach (var c in contours)
                    AvailableContours.Add(c);

                SetStatus(GenerationStatus.Idle, $"Загружено {contours.Count} контуров.");
            }
            catch (OperationCanceledException) { SetStatus(GenerationStatus.Idle, "Отменено."); }
            catch (PlanningApiException ex) { SetStatus(GenerationStatus.Error, $"API: {ex.Message}"); PluginLogger.Error(ex.Message, ex); }
            catch (Exception ex) { SetStatus(GenerationStatus.Error, $"Ошибка: {ex.Message}"); PluginLogger.Error("Ошибка загрузки контуров", ex); }
        }

        // ═══════════════════════════════════════════
        //  Методы: загрузка контура
        // ═══════════════════════════════════════════

        private async Task LoadSelectedContourAsync()
        {
            if (SelectedContourSummary == null) return;

            try
            {
                SetStatus(GenerationStatus.Loading, $"Загрузка контура '{SelectedContourSummary.Name}'…");
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                var contour = await _apiClient!.GetContourAsync(SelectedContourSummary.Id, _cts.Token);

                // Конвертация единиц
                contour = UnitConverter.ConvertToMeters(contour);

                // Валидация
                var validation = _validator.Validate(contour);
                ValidationResult = validation;

                if (!validation.IsValid)
                {
                    SetStatus(GenerationStatus.Error, "Контур не прошёл валидацию. Исправьте геометрию или выберите другой.");
                    return;
                }

                CurrentContour = contour;

                // Очищаем предыдущие варианты при смене контура
                Variants.Clear();
                AllVariants.Clear();
                SelectedVariant = null;

                // Отрисовка в Revit — прямо в модели, без экспорта
                var doc = _commandData.Application.ActiveUIDocument.Document;
                var view = doc.ActiveView;
                var level = GetActiveLevel(doc);
                _elementCreator.DrawContour(doc, view, contour, level);

                var geoNote = contour.HasCurvedGeometry
                    ? " (неортогональная/органичная форма)"
                    : " (ортогональная форма)";

                SetStatus(GenerationStatus.Idle, $"Контур '{contour.Name}' загружен и отображён{geoNote}.");
                EventAggregator.Instance.Publish(new ContourLoadedEvent { Contour = contour });
            }
            catch (OperationCanceledException) { SetStatus(GenerationStatus.Idle, "Отменено."); }
            catch (PlanningApiException ex) { SetStatus(GenerationStatus.Error, $"API: {ex.Message}"); }
            catch (Exception ex) { SetStatus(GenerationStatus.Error, $"Ошибка: {ex.Message}"); PluginLogger.Error("Ошибка загрузки контура", ex); }
        }

        // ═══════════════════════════════════════════
        //  Методы: пакетная генерация с прогрессом
        // ═══════════════════════════════════════════

        private async Task GenerateAsync()
        {
            if (CurrentContour == null) return;

            try
            {
                var sw = Stopwatch.StartNew();
                GenerationProgress = 0;
                GenerationElapsed = string.Empty;
                SetStatus(GenerationStatus.Generating,
                    $"Пакетная генерация {GenerationParams.VariantCount} вариантов…");
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                // Прогресс: 10% — отправка, 80% — ожидание, 10% — обработка
                GenerationProgress = 10;
                GenerationElapsed = "Отправка запроса…";

                var variants = await _apiClient!.GenerateLayoutsAsync(
                    CurrentContour.Id, GenerationParams, _cts.Token);

                GenerationProgress = 90;
                GenerationElapsed = $"Получено {variants.Count} вариантов, генерация миниатюр…";

                // Генерация SVG-миниатюр для каталожного отображения
                ThumbnailGenerator.GenerateThumbnails(variants, CurrentContour);

                // Сохраняем в историю контура
                CurrentContour.AddGenerationResult(variants);

                // Обновляем текущие варианты
                Variants.Clear();
                foreach (var v in variants)
                    Variants.Add(v);

                // Обновляем общий список (все генерации по контуру)
                AllVariants.Clear();
                foreach (var v in CurrentContour.GetAllVariants())
                    AllVariants.Add(v);

                GenerationProgress = 100;
                sw.Stop();
                GenerationElapsed = $"Готово за {sw.Elapsed.TotalSeconds:F1} сек";

                if (Variants.Any())
                    SelectedVariant = Variants.First();

                OnPropertyChanged(nameof(GenerationHistoryInfo));
                OnPropertyChanged(nameof(DisplayedVariants));
                OnPropertyChanged(nameof(VariantNavigationInfo));

                SetStatus(GenerationStatus.Completed,
                    $"Получено {variants.Count} вариантов за {sw.Elapsed.TotalSeconds:F1} сек. " +
                    $"Всего по контуру: {CurrentContour.TotalGeneratedVariants}.");
                EventAggregator.Instance.Publish(new GenerationCompletedEvent { Variants = variants });
            }
            catch (OperationCanceledException)
            {
                GenerationProgress = 0;
                SetStatus(GenerationStatus.Idle, "Генерация отменена.");
            }
            catch (PlanningApiException ex) { SetStatus(GenerationStatus.Error, $"API: {ex.Message}"); }
            catch (Exception ex) { SetStatus(GenerationStatus.Error, $"Ошибка генерации: {ex.Message}"); PluginLogger.Error("Ошибка генерации", ex); }
        }

        // ═══════════════════════════════════════════
        //  Методы: предпросмотр и применение
        // ═══════════════════════════════════════════

        private void PreviewVariant(LayoutVariant variant)
        {
            try
            {
                var doc = _commandData.Application.ActiveUIDocument.Document;
                var view = doc.ActiveView;
                var level = GetActiveLevel(doc);
                _elementCreator.DrawLayoutPreview(doc, view, variant, level);
                OnPropertyChanged(nameof(VariantNavigationInfo));
                EventAggregator.Instance.Publish(new VariantSelectedEvent { Variant = variant });
            }
            catch (Exception ex)
            {
                PluginLogger.Error("Ошибка предпросмотра", ex);
                StatusMessage = $"Ошибка предпросмотра: {ex.Message}";
            }
        }

        private void ApplySelectedVariant()
        {
            if (SelectedVariant == null) return;
            try
            {
                var doc = _commandData.Application.ActiveUIDocument.Document;
                var level = GetActiveLevel(doc);
                _elementCreator.ApplyLayout(doc, SelectedVariant, level);
                SetStatus(GenerationStatus.Completed,
                    $"Вариант #{SelectedVariantIndex} применён (разделители + помещения).");
            }
            catch (Exception ex)
            {
                SetStatus(GenerationStatus.Error, $"Ошибка применения: {ex.Message}");
                PluginLogger.Error("Ошибка применения варианта", ex);
            }
        }

        private void ApplySelectedVariantWithWalls()
        {
            if (SelectedVariant == null) return;
            try
            {
                var doc = _commandData.Application.ActiveUIDocument.Document;
                var level = GetActiveLevel(doc);

                var wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault();

                if (wallType == null)
                {
                    SetStatus(GenerationStatus.Error, "Не найден тип стен в проекте.");
                    return;
                }

                _elementCreator.ApplyLayoutWithWalls(doc, SelectedVariant, level, wallType);
                SetStatus(GenerationStatus.Completed,
                    $"Вариант #{SelectedVariantIndex} применён со стенами.");
            }
            catch (Exception ex)
            {
                SetStatus(GenerationStatus.Error, $"Ошибка: {ex.Message}");
                PluginLogger.Error("Ошибка применения со стенами", ex);
            }
        }

        // ═══════════════════════════════════════════
        //  Методы: настройки и управление
        // ═══════════════════════════════════════════

        private void SaveSettings()
        {
            _configService.Save(Settings);
            InitializeApiClient();
            StatusMessage = "Настройки сохранены.";
        }

        private void CancelOperation()
        {
            _cts?.Cancel();
            GenerationProgress = 0;
            SetStatus(GenerationStatus.Idle, "Операция отменена.");
        }

        private void SetStatus(GenerationStatus status, string message)
        {
            Status = status;
            StatusMessage = message;
            EventAggregator.Instance.Publish(new StatusChangedEvent { Status = status, Message = message });
        }

        private Level GetActiveLevel(Document doc)
        {
            if (doc.ActiveView.GenLevel != null)
                return doc.ActiveView.GenLevel;

            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();
        }

        public void Dispose()
        {
            _cts?.Dispose();
            // PlanningApiClient implements IDisposable; MockPlanningApiClient does not.
            // Conditional cast is intentional for polymorphic disposal.
            (_apiClient as IDisposable)?.Dispose();
        }
    }
}
