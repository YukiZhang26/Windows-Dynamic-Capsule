using DynamicCapsule.Models;

namespace DynamicCapsule.Services;

internal sealed class CapsuleEventScheduler : IDisposable
{
    private const int MaximumEvents = 100;

    private readonly Dictionary<string, CapsuleEvent> _events =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly System.Threading.Timer _expirationTimer;

    private CapsuleEvent? _activeEvent;
    private CapsuleEvent? _secondaryEvent;
    private string? _pinnedEventId;
    private bool _isPaused;
    private bool _disposed;

    internal CapsuleEventScheduler()
    {
        _expirationTimer = new System.Threading.Timer(
            _ => RemoveExpiredEvents(),
            null,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250));
    }

    internal event Action<CapsuleEvent?>? ActiveEventChanged;
    internal event Action<CapsuleEvent?, CapsuleEvent?>?
        PresentedEventsChanged;

    internal CapsuleEvent? ActiveEvent
    {
        get
        {
            lock (_gate)
            {
                return _activeEvent;
            }
        }
    }

    internal CapsuleEvent? SecondaryEvent
    {
        get
        {
            lock (_gate)
            {
                return _secondaryEvent;
            }
        }
    }

    internal string? PinnedEventId
    {
        get
        {
            lock (_gate)
            {
                return _pinnedEventId;
            }
        }
    }

    internal void Publish(CapsuleEvent capsuleEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CapsuleEvent? next;
        CapsuleEvent? secondary;
        var activeChanged = false;
        var presentationChanged = false;
        lock (_gate)
        {
            _events[capsuleEvent.EventId] = capsuleEvent;
            PruneToCapacity();
            (next, secondary) = SelectPresentedEvents(
                DateTimeOffset.UtcNow);
            activeChanged = next != _activeEvent;
            presentationChanged =
                activeChanged || secondary != _secondaryEvent;
            _activeEvent = next;
            _secondaryEvent = secondary;
        }

        NotifyPresentationChanged(
            activeChanged,
            presentationChanged,
            next,
            secondary);
    }

    internal void Remove(string eventId)
    {
        if (_disposed)
        {
            return;
        }

        CapsuleEvent? next;
        CapsuleEvent? secondary;
        var activeChanged = false;
        var presentationChanged = false;
        lock (_gate)
        {
            _events.Remove(eventId);
            if (string.Equals(
                    _pinnedEventId,
                    eventId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _pinnedEventId = null;
            }

            (next, secondary) = SelectPresentedEvents(
                DateTimeOffset.UtcNow);
            activeChanged = next != _activeEvent;
            presentationChanged =
                activeChanged || secondary != _secondaryEvent;
            _activeEvent = next;
            _secondaryEvent = secondary;
        }

        NotifyPresentationChanged(
            activeChanged,
            presentationChanged,
            next,
            secondary);
    }

    internal void Dismiss(string eventId)
    {
        Remove(eventId);
    }

    internal bool Pin(string eventId)
    {
        if (_disposed)
        {
            return false;
        }

        CapsuleEvent? next;
        CapsuleEvent? secondary;
        var activeChanged = false;
        var presentationChanged = false;
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var capsuleEvent)
                || capsuleEvent.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            _pinnedEventId = eventId;
            (next, secondary) = SelectPresentedEvents(
                DateTimeOffset.UtcNow);
            activeChanged = next != _activeEvent;
            presentationChanged =
                activeChanged || secondary != _secondaryEvent;
            _activeEvent = next;
            _secondaryEvent = secondary;
        }

        NotifyPresentationChanged(
            activeChanged,
            presentationChanged,
            next,
            secondary);

        return true;
    }

    internal void Unpin()
    {
        if (_disposed)
        {
            return;
        }

        CapsuleEvent? next;
        CapsuleEvent? secondary;
        var activeChanged = false;
        var presentationChanged = false;
        lock (_gate)
        {
            if (_pinnedEventId is null)
            {
                return;
            }

            _pinnedEventId = null;
            (next, secondary) = SelectPresentedEvents(
                DateTimeOffset.UtcNow);
            activeChanged = next != _activeEvent;
            presentationChanged =
                activeChanged || secondary != _secondaryEvent;
            _activeEvent = next;
            _secondaryEvent = secondary;
        }

        NotifyPresentationChanged(
            activeChanged,
            presentationChanged,
            next,
            secondary);
    }

    internal bool IsPinned(string eventId)
    {
        lock (_gate)
        {
            return string.Equals(
                _pinnedEventId,
                eventId,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    internal void ApplyPolicy(Func<CapsuleEvent, CapsuleEvent?> transform)
    {
        if (_disposed)
        {
            return;
        }

        CapsuleEvent? next;
        CapsuleEvent? secondary;
        var activeChanged = false;
        var presentationChanged = false;
        lock (_gate)
        {
            foreach (var capsuleEvent in _events.Values.ToArray())
            {
                var transformed = transform(capsuleEvent);
                if (transformed is null)
                {
                    _events.Remove(capsuleEvent.EventId);
                }
                else
                {
                    _events[capsuleEvent.EventId] = transformed;
                }
            }

            if (_pinnedEventId is not null
                && !_events.ContainsKey(_pinnedEventId))
            {
                _pinnedEventId = null;
            }

            (next, secondary) = SelectPresentedEvents(
                DateTimeOffset.UtcNow);
            activeChanged = next != _activeEvent;
            presentationChanged =
                activeChanged || secondary != _secondaryEvent;
            _activeEvent = next;
            _secondaryEvent = secondary;
        }

        NotifyPresentationChanged(
            activeChanged,
            presentationChanged,
            next,
            secondary);
    }

    internal void SetPaused(bool isPaused)
    {
        if (_disposed)
        {
            return;
        }

        CapsuleEvent? next;
        CapsuleEvent? secondary;
        var activeChanged = false;
        var presentationChanged = false;
        lock (_gate)
        {
            if (_isPaused == isPaused)
            {
                return;
            }

            _isPaused = isPaused;
            (next, secondary) = SelectPresentedEvents(
                DateTimeOffset.UtcNow);
            activeChanged = next != _activeEvent;
            presentationChanged =
                activeChanged || secondary != _secondaryEvent;
            _activeEvent = next;
            _secondaryEvent = secondary;
        }

        NotifyPresentationChanged(
            activeChanged,
            presentationChanged,
            next,
            secondary);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _expirationTimer.Dispose();
        lock (_gate)
        {
            _events.Clear();
            _activeEvent = null;
            _secondaryEvent = null;
            _pinnedEventId = null;
        }
    }

    private void RemoveExpiredEvents()
    {
        if (_disposed)
        {
            return;
        }

        CapsuleEvent? next;
        CapsuleEvent? secondary;
        var activeChanged = false;
        var presentationChanged = false;
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var expiredIds = _events.Values
                .Where(capsuleEvent => capsuleEvent.ExpiresAt <= now)
                .Select(capsuleEvent => capsuleEvent.EventId)
                .ToArray();

            foreach (var eventId in expiredIds)
            {
                _events.Remove(eventId);
            }

            if (_pinnedEventId is not null
                && !_events.ContainsKey(_pinnedEventId))
            {
                _pinnedEventId = null;
            }

            (next, secondary) = SelectPresentedEvents(now);
            activeChanged = next != _activeEvent;
            presentationChanged =
                activeChanged || secondary != _secondaryEvent;
            _activeEvent = next;
            _secondaryEvent = secondary;
        }

        NotifyPresentationChanged(
            activeChanged,
            presentationChanged,
            next,
            secondary);
    }

    private (CapsuleEvent? Primary, CapsuleEvent? Secondary)
        SelectPresentedEvents(DateTimeOffset now)
    {
        if (_isPaused)
        {
            return (null, null);
        }

        var activeEvents = _events.Values
            .Where(capsuleEvent => capsuleEvent.ExpiresAt is null
                                   || capsuleEvent.ExpiresAt > now)
            .ToArray();
        var primary = SelectActiveEvent(activeEvents);
        var secondary = activeEvents
            .Where(capsuleEvent =>
                primary is null
                || !string.Equals(
                    capsuleEvent.EventId,
                    primary.EventId,
                    StringComparison.OrdinalIgnoreCase))
            .Where(IsSecondaryCandidate)
            .OrderByDescending(capsuleEvent => capsuleEvent.Priority)
            .ThenByDescending(capsuleEvent => capsuleEvent.CreatedAt)
            .FirstOrDefault();

        return (primary, secondary);
    }

    private CapsuleEvent? SelectActiveEvent(
        IReadOnlyCollection<CapsuleEvent> activeEvents)
    {
        var criticalEvent = activeEvents
            .Where(capsuleEvent =>
                capsuleEvent.Priority == CapsuleEventPriority.Critical)
            .OrderByDescending(capsuleEvent => capsuleEvent.Priority)
            .ThenByDescending(capsuleEvent => capsuleEvent.CreatedAt)
            .FirstOrDefault();
        if (criticalEvent is not null)
        {
            return criticalEvent;
        }

        if (_pinnedEventId is not null)
        {
            var pinnedEvent = activeEvents.FirstOrDefault(capsuleEvent =>
                string.Equals(
                    capsuleEvent.EventId,
                    _pinnedEventId,
                    StringComparison.OrdinalIgnoreCase));
            if (pinnedEvent is not null)
            {
                return pinnedEvent;
            }

            _pinnedEventId = null;
        }

        return activeEvents
            .OrderByDescending(capsuleEvent => capsuleEvent.Priority)
            .ThenByDescending(capsuleEvent => capsuleEvent.CreatedAt)
            .FirstOrDefault();
    }

    private static bool IsSecondaryCandidate(CapsuleEvent capsuleEvent)
    {
        return capsuleEvent.Kind is
                   CapsuleEventKind.TaskProgress
                   or CapsuleEventKind.Timer
                   or CapsuleEventKind.Stopwatch
               && capsuleEvent.TaskState == CapsuleTaskState.Running;
    }

    private void NotifyPresentationChanged(
        bool activeChanged,
        bool presentationChanged,
        CapsuleEvent? activeEvent,
        CapsuleEvent? secondaryEvent)
    {
        if (activeChanged)
        {
            ActiveEventChanged?.Invoke(activeEvent);
        }

        if (presentationChanged)
        {
            PresentedEventsChanged?.Invoke(activeEvent, secondaryEvent);
        }
    }

    private void PruneToCapacity()
    {
        if (_events.Count <= MaximumEvents)
        {
            return;
        }

        foreach (var eventId in _events.Values
                     .Where(capsuleEvent => !string.Equals(
                         capsuleEvent.EventId,
                         _pinnedEventId,
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(capsuleEvent => capsuleEvent.Priority)
                     .ThenBy(capsuleEvent => capsuleEvent.CreatedAt)
                     .Take(_events.Count - MaximumEvents)
                     .Select(capsuleEvent => capsuleEvent.EventId)
                     .ToArray())
        {
            _events.Remove(eventId);
        }
    }
}
