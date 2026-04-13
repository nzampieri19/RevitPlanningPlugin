using System;
using System.Collections.Generic;

namespace RevitPlanningPlugin.Infrastructure
{
    /// <summary>
    /// Простой агрегатор событий для связи между модулями без прямых зависимостей.
    /// Используется как точка расширения: внешние модули (плагины, скрипты, тестовые harness)
    /// могут подписываться на события <see cref="ContourLoadedEvent"/>,
    /// <see cref="GenerationCompletedEvent"/>, <see cref="VariantSelectedEvent"/>
    /// и <see cref="StatusChangedEvent"/> через <see cref="Subscribe{T}"/>.
    ///
    /// В текущей версии плагина публикация событий активна, подписки добавляются
    /// по мере расширения функциональности.
    /// </summary>
    public class EventAggregator
    {
        private static readonly Lazy<EventAggregator> _instance = new(() => new EventAggregator());
        public static EventAggregator Instance => _instance.Value;

        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public void Subscribe<T>(Action<T> handler)
        {
            var type = typeof(T);
            if (!_handlers.ContainsKey(type))
                _handlers[type] = new List<Delegate>();
            _handlers[type].Add(handler);
        }

        public void Unsubscribe<T>(Action<T> handler)
        {
            var type = typeof(T);
            if (_handlers.ContainsKey(type))
                _handlers[type].Remove(handler);
        }

        public void Publish<T>(T message)
        {
            var type = typeof(T);
            if (!_handlers.ContainsKey(type)) return;
            foreach (var handler in _handlers[type].ToArray())
                ((Action<T>)handler)(message);
        }
    }

    // ——— События ———

    public class ContourLoadedEvent
    {
        public Models.Domain.BuildingContour Contour { get; set; } = null!;
    }

    public class GenerationCompletedEvent
    {
        public List<Models.Domain.LayoutVariant> Variants { get; set; } = new();
    }

    public class VariantSelectedEvent
    {
        public Models.Domain.LayoutVariant Variant { get; set; } = null!;
    }

    public class StatusChangedEvent
    {
        public Models.Enums.GenerationStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
