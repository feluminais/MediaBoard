using Avalonia.Threading;

namespace CozyCouch.Desktop.ViewModels;

/// <summary>
/// Runs an action on the UI thread at most once a period, however often it's asked for and from whatever thread: the
/// first ask at once, and anything asked meanwhile once when the period ends.
/// </summary>
internal sealed class UiThrottle(TimeProvider time, TimeSpan period, Action action)
{
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private bool _waiting;
    private bool _asked;

    public void Request()
    {
        lock (_gate)
        {
            if (_waiting)
            {
                _asked = true;
                return;
            }

            _waiting = true;
            Arm();
        }

        Dispatcher.UIThread.Post(action);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _waiting = false;
            _asked = false;
        }
    }

    private void OnPeriodEnded()
    {
        lock (_gate)
        {
            if (!_waiting) return;
            if (!_asked)
            {
                _waiting = false;
                return;
            }

            _asked = false;
            Arm();
        }

        Dispatcher.UIThread.Post(action);
    }

    private void Arm()
    {
        if (_timer is null) _timer = time.CreateTimer(_ => OnPeriodEnded(), null, period, Timeout.InfiniteTimeSpan);
        else _timer.Change(period, Timeout.InfiniteTimeSpan);
    }
}
