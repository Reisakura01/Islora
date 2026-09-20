using System;
using System.Threading.Tasks;
using Islora.Models;
using Windows.Media.Control;

namespace Islora.Services;

/// <summary>
/// 媒体实时活动：通过系统级媒体会话（SMTC）获取全局正在播放的曲名 / 歌手。
/// </summary>
internal sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private double _lastRawPos = -1;
    private DateTime _lastRawTime;
    private bool _wasPlaying;   // 上一次的播放状态（用于在「暂停 → 播放」时重置外推基准）
    private readonly System.Threading.SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>媒体会话变化（null 表示当前无会话）。</summary>
    public event Action<MediaSessionInfo?>? SessionChanged;

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        await RefreshAsync();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => _ = RefreshAsync();

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => _ = RefreshAsync();

    // 同一播放器内切歌：会话不变、媒体属性变化 → 必须监听会话级事件才能感知
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        // 串行化：避免多个 WinRT 事件并发触发导致旧会话覆盖新会话、UI 显示错标题
        await _refreshLock.WaitAsync();
        try
        {
            var session = _manager?.GetCurrentSession();
            AttachSession(session);
            if (session is null)
            {
                SessionChanged?.Invoke(null);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            SessionChanged?.Invoke(new MediaSessionInfo(
                props?.Title ?? string.Empty,
                props?.Artist ?? string.Empty,
                props?.Thumbnail,
                playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
        }
        catch
        {
            // 单路刷新失败不影响其它路（避免 UI 停在旧状态）
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // ---- 播放控制（展开卡片控制按钮调用；无会话时静默忽略） ----

    public async Task TogglePlayPauseAsync()
    {
        if (_session is null) return;
        try { await _session.TryTogglePlayPauseAsync(); }
        catch { }
    }

    public async Task SkipNextAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipNextAsync(); }
        catch { }
    }

    public async Task SkipPreviousAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipPreviousAsync(); }
        catch { }
    }

    /// <summary>跳转到指定进度（秒）。源支持才生效，否则静默忽略。</summary>
    public async Task SeekAsync(double seconds)
    {
        if (_session is null) return;
        try { await _session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks); }
        catch { }
    }

    // ---- 播放进度（系统媒体时间轴） ----

    /// <summary>返回当前播放进度（秒）：(当前, 总时长)；无会话时返回 null。总时长未知时返回 0。</summary>
    public (double Position, double Duration)? GetProgress()
    {
        // 每次取最新当前会话，避免用到过期会话
        var session = _manager?.GetCurrentSession();
        if (session is null) return null;
        try
        {
            var t = session.GetTimelineProperties();
            var end = t.EndTime.TotalSeconds;
            var max = t.MaxSeekTime.TotalSeconds;
            var rawPos = t.Position.TotalSeconds;
            // 总时长优先 EndTime，其次 MaxSeekTime，都未知则为 0
            var dur = end > 0 ? end : (max > 0 ? max : 0);
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var now = DateTime.UtcNow;

            // 重置外推基准的时机：播放状态发生变化（关键在于「暂停 → 播放」），
            // 或者源上报的位置发生了变化。
            //
            // 这里踩过两次坑，记录清楚：
            // 1) 基准原来只在 rawPos 变化时刷新 —— 暂停期间不刷新，恢复播放时就把**整个暂停时长**
            //    加进了位置（暂停 10 分钟 → +600 秒），进度条瞬间跳到曲末。
            // 2) 于是给外推量加了个「最多 2 秒」的上限 —— 结果误伤了另一类源：
            //    只上报总时长、位置长期不变的音乐客户端（rawPos 恒为 0），
            //    基准永不刷新 → 外推量 2 秒后超过上限 → 位置永远停住，
            //    表现为「进度条填充卡死不动」。
            // 正确解法是上面第 1 条的真正病根（状态切换时重置基准），而不是限制外推量。
            if (playing != _wasPlaying || rawPos != _lastRawPos)
            {
                _wasPlaying = playing;
                _lastRawPos = rawPos;
                _lastRawTime = now;
            }

            var pos = rawPos;
            if (dur > 0 && playing)
            {
                // 不设人为上限：基准已在状态/位置变化时刷新，外推量自然很小；
                // 对恒定不更新的源，这里正好提供连续前进。最终由下方的 dur 截断兜底。
                var extra = (now - _lastRawTime).TotalSeconds;
                if (extra > 0) pos = rawPos + extra;
            }

            if (pos < 0) pos = 0;
            if (dur > 0 && pos > dur) pos = dur;
            return (pos, dur);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>跟随当前会话：订阅其切歌/播放状态事件（换会话时先退订旧会话，避免重复触发）。</summary>
    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        _session = session;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
    }
}
