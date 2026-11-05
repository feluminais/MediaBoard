using MediaBoard.Core.Remote;

namespace MediaBoard.Core.Settings;

public sealed record AppSettings
{
    public string? TmdbKey { get; init; }

    public string? DefaultPlayerId { get; init; }

    public string? CustomPlayerPath { get; init; }

    /// <summary>
    /// Follow an mpv, VLC or MPC-HC the viewer started without MediaBoard, when it was set up to listen (mpv's
    /// <c>input-ipc-server</c>, the web interface of the others), and record the progress of the library files it plays.
    /// </summary>
    public bool FollowOtherPlayers { get; init; } = true;

    /// <summary>The port of the web interface of the viewer's own MPC-HC or MPC-BE (their default).</summary>
    public int MpcWebPort { get; init; } = 13579;

    /// <summary>The port of the web interface of the viewer's own VLC.</summary>
    public int VlcWebPort { get; init; } = 8080;

    /// <summary>The password of the web interface of the viewer's own VLC; VLC asks for one, and without it VLC is not followed.</summary>
    public string? VlcWebPassword { get; init; }

    /// <summary>Full path to ffprobe.exe; null looks "ffprobe" up on PATH.</summary>
    public string? FfprobePath { get; init; }

    /// <summary>The size of text and controls, in percent of the default, on top of Windows' own display scaling.</summary>
    public int UiSizePercent { get; init; } = 100;

    /// <summary>The size of the poster cards in the grids and Home rows.</summary>
    public PosterSize PosterSize { get; init; } = PosterSize.Medium;

    /// <summary>Text and posters step up in a wide window (UiSizes in the desktop app); off keeps the chosen sizes at every width.</summary>
    public bool GrowWithWindow { get; init; } = true;

    public SharingSettings Sharing { get; init; } = new();

    /// <summary>The server this PC connects to as a viewer, or null if it has never joined one.</summary>
    public StoredConnection? Connection { get; init; }
}

public sealed record SharingSettings
{
    /// <summary>Sharing came up the last time it was asked to, so startup brings it back.</summary>
    public bool On { get; init; }

    public int Port { get; init; } = 47880;

    /// <summary>The name people see; null is "&lt;Windows user&gt;'s library".</summary>
    public string? ServerName { get; init; }

    /// <summary>What reaches this PC from the internet: a host name or address, with a port if the router forwards a different one.</summary>
    public string? PublicAddress { get; init; }

    /// <summary>What reaches this PC from the home network, in the same form. Invites try it first.</summary>
    public string? HomeAddress { get; init; }

    public bool StartWithWindows { get; init; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PosterSize>))]
public enum PosterSize
{
    Small,
    Medium,
    Large,
}
