namespace MediaBoard.Core.Settings;

public sealed record AppSettings
{
    public string? TmdbKey { get; init; }

    public string? DefaultPlayerId { get; init; }

    public string? CustomPlayerPath { get; init; }

    /// <summary>
    /// Follow an mpv or VLC the viewer started without MediaBoard, when it was set up to listen (mpv's
    /// <c>input-ipc-server</c>, VLC's web interface), and record the progress of the library files it plays.
    /// </summary>
    public bool FollowOtherPlayers { get; init; } = true;

    /// <summary>The port of the web interface of the viewer's own VLC.</summary>
    public int VlcWebPort { get; init; } = 8080;

    /// <summary>The password of the web interface of the viewer's own VLC; VLC asks for one, and without it VLC is not followed.</summary>
    public string? VlcWebPassword { get; init; }

    /// <summary>Full path to ffprobe.exe; null looks "ffprobe" up on PATH.</summary>
    public string? FfprobePath { get; init; }
}
