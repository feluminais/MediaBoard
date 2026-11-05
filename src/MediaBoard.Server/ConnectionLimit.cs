using System.Net;
using Microsoft.AspNetCore.Connections;

namespace MediaBoard.Server;

/// <summary>Keeps one address from holding the whole server's connections. Counts by <see cref="ClientAddress"/>, as the sign-in guard does.</summary>
/// <param name="perAddress">How many one address may hold at once.</param>
internal sealed class ConnectionLimit(int perAddress)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _open = [];

    public ConnectionDelegate Wrap(ConnectionDelegate next) => async connection =>
    {
        var address = ClientAddress.Of((connection.RemoteEndPoint as IPEndPoint)?.Address);
        if (!TryEnter(address))
        {
            connection.Abort();
            return;
        }

        try
        {
            await next(connection);
        }
        finally
        {
            Leave(address);
        }
    };

    private bool TryEnter(string address)
    {
        lock (_gate)
        {
            var open = _open.GetValueOrDefault(address);
            if (open >= perAddress) return false;

            _open[address] = open + 1;
            return true;
        }
    }

    private void Leave(string address)
    {
        lock (_gate)
        {
            var left = _open[address] - 1;
            if (left == 0) _open.Remove(address);
            else _open[address] = left;
        }
    }
}
