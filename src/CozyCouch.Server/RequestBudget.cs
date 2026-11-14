namespace CozyCouch.Server;

/// <summary>How much of something costly each account may use in a stretch of time. Any thread may call it.</summary>
/// <param name="allowed">The uses per stretch.</param>
/// <param name="every">The stretch.</param>
/// <param name="time">The clock.</param>
internal sealed class RequestBudget(int allowed, TimeSpan every, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, (long Started, int Used)> _accounts = [];

    /// <returns>False when this account has used up the stretch.</returns>
    public bool TryTake(int userId)
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (!_accounts.TryGetValue(userId, out var account) || time.GetElapsedTime(account.Started, now) >= every)
            {
                _accounts[userId] = (now, 1);
                return true;
            }

            if (account.Used >= allowed) return false;

            _accounts[userId] = account with { Used = account.Used + 1 };
            return true;
        }
    }
}
