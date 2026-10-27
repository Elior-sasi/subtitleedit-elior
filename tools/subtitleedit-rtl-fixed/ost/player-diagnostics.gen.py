"""Generates player-diagnostics.patch.json (anchors + inserted code) for apply-player-diagnostics.ps1.

Each edit: file, mode (after|before|replace), anchor (must occur exactly once), text.
Run: python3 player-diagnostics.gen.py  (writes the JSON next to this script).
"""
import json
import os

D = "Nikse.SubtitleEdit.Logic.VideoPlayers.PlayerDiagnostics"
P = "src/ui/Logic/VideoPlayers/LibMpvDynamic/LibMpvDynamicPlayer.cs"
C = "src/ui/Controls/VideoPlayer/VideoPlayerControl.cs"
M = "src/ui/Features/Main/MainViewModel.cs"

edits = []


def e(file, mode, anchor, text):
    edits.append({"file": file, "mode": mode, "anchor": anchor, "text": text})


# ---------------------------------------------------------------- LibMpvDynamicPlayer
e(P, "after", "    private volatile bool _coreInitialized;\n", f"""
    // Custom build: player lifecycle diagnostics (see PlayerDiagnostics). Cached state only -
    // logging never calls into the core, so it cannot block on a busy core itself.
    private readonly long _diagId = {D}.NewPlayerId();
    private int _diagLoadSeq;
    private int _diagMpvMessages;
    private volatile int _diagIdleActive = -1;
    private volatile int _diagLoadsInFlight;
    private const ulong ObserveIdIdleActive = 6;
    private const int MPV_EVENT_START_FILE = 6;
    private const int MPV_EVENT_END_FILE = 7;
    private const int MPV_EVENT_FILE_LOADED = 8;
    private static readonly byte[] PropertyNameIdleActive = GetUtf8Bytes("idle-active");

    /// <summary>Diagnostics id of this player instance (custom build).</summary>
    public long DiagnosticsId => _diagId;

    /// <summary>One-line cached state for the diagnostics log. Never touches the core.</summary>
    internal string DiagnosticsState()
    {{
        var thread = _eventThread;
        return $"p={{_diagId}} disposed={{_disposed}} core={{_coreInitialized}} mpv={{(_mpv != IntPtr.Zero)}} " +
               $"loop={{_eventLoopActive}} loopAlive={{(thread?.IsAlive ?? false)}} idle={{_diagIdleActive}} " +
               $"pauseCache={{_observedPause}} dur={{{D}.N(BitConverter.Int64BitsToDouble(Interlocked.Read(ref _observedDurationBits)))}} " +
               $"loadsInFlight={{_diagLoadsInFlight}} pending={{(_pendingLoadFileName != null)}} file={{{D}.MediaTag(_fileName)}}";
    }}

    private void DiagMpvEvent(MpvEvent mpvEvent)
    {{
        try
        {{
            switch (mpvEvent.eventId)
            {{
                case MPV_EVENT_START_FILE:
                    {D}.Log("mpv-evt", $"p={{_diagId}} START_FILE");
                    break;
                case MPV_EVENT_FILE_LOADED:
                    {D}.Log("mpv-evt", $"p={{_diagId}} FILE_LOADED");
                    break;
                case MPV_EVENT_END_FILE:
                    var reason = mpvEvent.data != IntPtr.Zero ? Marshal.ReadInt32(mpvEvent.data, 0) : -1;
                    var error = mpvEvent.data != IntPtr.Zero ? Marshal.ReadInt32(mpvEvent.data, 4) : 0;
                    {D}.Log("mpv-evt", $"p={{_diagId}} END_FILE reason={{{D}.EndFileReason(reason)}} error={{error}} ({{(error < 0 ? GetErrorString(error) : "ok")}})");
                    break;
                case MPV_EVENT_SHUTDOWN:
                    {D}.Log("mpv-evt", $"p={{_diagId}} SHUTDOWN - event loop ends");
                    break;
            }}
        }}
        catch
        {{
            // diagnostics only
        }}
    }}

    private void DiagMpvMessage(IntPtr data)
    {{
        // Separate cap from the error-log forwarder (50 per core), which can be used up by
        // harmless warnings long before a failing load says why it failed.
        if (_diagMpvMessages >= 400)
        {{
            return;
        }}

        try
        {{
            var message = Marshal.PtrToStructure<MpvEventLogMessage>(data);
            var level = message.level == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(message.level) ?? string.Empty;
            if (level is not ("fatal" or "error" or "warn"))
            {{
                return;
            }}

            var prefix = message.prefix == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(message.prefix);
            var text = message.text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(message.text);
            _diagMpvMessages++;
            {D}.Log("mpv-msg", $"p={{_diagId}} [{{level}}] {{prefix}}: {{{D}.Redact(text)}}");
        }}
        catch
        {{
            // diagnostics only
        }}
    }}
""")

# event loop: every event, wait_event exceptions, idle-active
e(P, "replace", """                eventPtr = waitEvent(handle, 1.0);
            }
            catch
            {
                break;
            }""", f"""                eventPtr = waitEvent(handle, 1.0);
            }}
            catch (Exception diagException)
            {{
                {D}.Log("mpv-evt", $"p={{_diagId}} event loop ended by exception {{diagException.GetType().Name}}: {{{D}.Redact(diagException.Message)}}");
                break;
            }}""")
e(P, "before", """            if (mpvEvent.eventId == MPV_EVENT_SHUTDOWN)
            {
                break;
            }""", """            DiagMpvEvent(mpvEvent);
""")
e(P, "before", "                    case ObserveIdTimePos:\n", f"""                    case ObserveIdIdleActive:
                        var idle = property.format == MPV_FORMAT_FLAG && property.data != IntPtr.Zero ? Marshal.ReadInt32(property.data) : -1;
                        if (idle != _diagIdleActive)
                        {{
                            _diagIdleActive = idle;
                            {D}.Log("mpv-evt", $"p={{_diagId}} idle-active={{idle}}");
                        }}

                        break;
""")
e(P, "after", "        _eventLoopActive = true;\n", f"""        try
        {{
            // Diagnostics only; failing to observe it changes nothing else.
            _mpvObserveProperty(handle, ObserveIdIdleActive, PropertyNameIdleActive, MPV_FORMAT_FLAG);
        }}
        catch
        {{
            // ignore
        }}

        {D}.Log("player", $"p={{_diagId}} event loop started");
""")
e(P, "after", """        _eventThread = null;
        _eventLoopActive = false;
""", f"""        {D}.Log("player", $"p={{_diagId}} event loop stop requested");
""")
e(P, "after", """    private void ForwardMpvLogMessage(IntPtr data)
    {
""", """        DiagMpvMessage(data);
""")
e(P, "after", """    public void Dispose()
    {
""", f"""        {D}.Log("player", $"Dispose {{DiagnosticsState()}}");
""")

# LoadFile
e(P, "replace", """    public async Task LoadFile(string path, double startPositionSeconds = 0)
    {
        EnsureNotDisposed();
""", f"""    public async Task LoadFile(string path, double startPositionSeconds = 0)
    {{
        var diagSeq = Interlocked.Increment(ref _diagLoadSeq);
        var diagStart = {D}.Now;
        Interlocked.Increment(ref _diagLoadsInFlight);
        {D}.Log("load", $"#{{diagSeq}} begin media={{{D}.MediaTag(path)}} start={{{D}.N(startPositionSeconds)}} {{DiagnosticsState()}}");
        try
        {{
            await LoadFileCore(path, startPositionSeconds, diagSeq);
        }}
        catch (Exception diagException)
        {{
            {D}.Log("load", $"#{{diagSeq}} threw {{diagException.GetType().Name}}: {{{D}.Redact(diagException.Message)}}");
            throw;
        }}
        finally
        {{
            Interlocked.Decrement(ref _diagLoadsInFlight);
            {D}.Log("load", $"#{{diagSeq}} returned after {{{D}.Now - diagStart}} ms {{DiagnosticsState()}}");
        }}
    }}

    private async Task LoadFileCore(string path, double startPositionSeconds, int diagSeq)
    {{
        EnsureNotDisposed();
""")
e(P, "replace", """        await WaitForCoreInitializedAsync();

        if (!_coreInitialized)
        {
            if (_disposed)
            {
                return;
            }
""", f"""        var diagCoreWait = {D}.Now;
        {D}.Log("load", $"#{{diagSeq}} waiting for core (core={{_coreInitialized}})");
        await WaitForCoreInitializedAsync();
        {D}.Log("load", $"#{{diagSeq}} core wait done after {{{D}.Now - diagCoreWait}} ms core={{_coreInitialized}} disposed={{_disposed}}");

        if (!_coreInitialized)
        {{
            if (_disposed)
            {{
                {D}.Log("load", $"#{{diagSeq}} not sent: player disposed while waiting for core");
                return;
            }}
""")
e(P, "before", """                Se.LogError("LibMpvDynamicPlayer LoadFile: mpv core not initialized yet - load of \\"" + path + "\\" deferred to core initialization");""",
  f"""                {D}.Log("load", $"#{{diagSeq}} not sent: deferred until the core initializes");
""")
e(P, "replace", """        if (DoMpvCommand("set", "pause", "yes") >= 0)
        {
            SetObservedPause(true);
        }
""", f"""        var diagPauseRc = DoMpvCommand("set", "pause", "yes");
        if (diagPauseRc >= 0)
        {{
            SetObservedPause(true);
        }}

        {D}.Log("load", $"#{{diagSeq}} set pause=yes rc={{diagPauseRc}}");
""")
e(P, "replace", """        var err = await Task.Run(() => DoMpvCommand("loadfile", loadPath));
        if (_disposed)
        {
            return;
        }
""", f"""        // A blocked native command cannot be cancelled; the watchdog only reports it.
        var diagCommandStart = {D}.Now;
        var diagCommandDone = 0;
        {D}.Log("load", $"#{{diagSeq}} loadfile sent media={{{D}.MediaTag(path)}} prefixed={{!string.Equals(loadPath, path, StringComparison.Ordinal)}}");
        using var diagWatchdog = new System.Threading.Timer(_ =>
        {{
            if (Volatile.Read(ref diagCommandDone) == 0)
            {{
                {D}.Log("load", $"#{{diagSeq}} loadfile STILL BLOCKED after {{{D}.Now - diagCommandStart}} ms {{DiagnosticsState()}}");
            }}
        }}, null, 5000, 15000);
        var err = await Task.Run(() => DoMpvCommand("loadfile", loadPath));
        Volatile.Write(ref diagCommandDone, 1);
        {D}.Log("load", $"#{{diagSeq}} loadfile returned rc={{err}} ({{(err < 0 ? GetErrorString(err) : "ok")}}) after {{{D}.Now - diagCommandStart}} ms disposed={{_disposed}}");
        if (_disposed)
        {{
            return;
        }}
""")

# Close / play commands
e(P, "replace", """        var err = DoMpvCommand("stop");
        if (err < 0)
        {
            Se.LogError(new InvalidOperationException(GetErrorString(err)), "LibMpvDynamicPlayer CloseFile");
        }
""", f"""        var diagStop = {D}.Now;
        {D}.Log("close", $"stop sent {{DiagnosticsState()}}");
        var err = DoMpvCommand("stop");
        {D}.Log("close", $"p={{_diagId}} stop returned rc={{err}} after {{{D}.Now - diagStop}} ms");
        if (err < 0)
        {{
            Se.LogError(new InvalidOperationException(GetErrorString(err)), "LibMpvDynamicPlayer CloseFile");
        }}
""")
e(P, "replace", """        var err = DoMpvCommand("cycle", "pause");
""", f"""        var err = DoMpvCommand("cycle", "pause");
        {D}.Log("play", $"cycle pause rc={{err}} {{DiagnosticsState()}}");
""")
e(P, "replace", """        var err = DoMpvCommand("set", "pause", "no");
""", f"""        var err = DoMpvCommand("set", "pause", "no");
        {D}.Log("play", $"set pause=no rc={{err}} {{DiagnosticsState()}}");
""")

# ---------------------------------------------------------------- VideoPlayerControl
e(C, "after", "        private IVideoPlayer _videoPlayerInstance;\n", f"""        private readonly long _diagId = {D}.NewControlId();
        private long _diagOpenGeneration;
        private int _diagOpensInFlight;

        /// <summary>Diagnostics id of this control (custom build).</summary>
        internal long DiagnosticsId => _diagId;

        private string DiagnosticsState() =>
            $"c={{_diagId}} disposed={{IsDisposed}} hasVideo={{_hasVideo}} openGen={{Interlocked.Read(ref _diagOpenGeneration)}} opensInFlight={{_diagOpensInFlight}} " +
            (_videoPlayerInstance is Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic.LibMpvDynamicPlayer mpv
                ? mpv.DiagnosticsState()
                : $"engine={{_videoPlayerInstance.GetType().Name}}");
""")
e(C, "replace", """        internal async Task Open(string videoFileName, double startPositionSeconds = 0)
        {
            if (IsDisposed)
            {
                return;
            }
""", f"""        internal async Task Open(string videoFileName, double startPositionSeconds = 0)
        {{
            var diagGeneration = {D}.NewOpenGeneration();
            Interlocked.Exchange(ref _diagOpenGeneration, diagGeneration);
            {D}.Log("open", $"g={{diagGeneration}} begin media={{{D}.MediaTag(videoFileName)}} start={{{D}.N(startPositionSeconds)}} {{DiagnosticsState()}}");
            if (IsDisposed)
            {{
                {D}.Log("open", $"g={{diagGeneration}} ignored: control disposed");
                return;
            }}
""")
e(C, "replace", """            await _videoPlayerInstance.LoadFile(videoFileName, startPositionSeconds);
""", f"""            var diagLoadStart = {D}.Now;
            Interlocked.Increment(ref _diagOpensInFlight);
            try
            {{
                await _videoPlayerInstance.LoadFile(videoFileName, startPositionSeconds);
            }}
            finally
            {{
                Interlocked.Decrement(ref _diagOpensInFlight);
            }}

            var diagLatest = Interlocked.Read(ref _diagOpenGeneration);
            {D}.Log("open", $"g={{diagGeneration}} LoadFile awaited {{{D}.Now - diagLoadStart}} ms" +
                (diagLatest != diagGeneration ? $" SUPERSEDED by g={{diagLatest}}" : string.Empty) + $" {{DiagnosticsState()}}");
""")
e(C, "after", """            _textBlockVideoFileName.Text = System.IO.Path.GetFileName(videoFileName);
            UpdateVideoFileNameMaxWidth();
""", f"""            {D}.Log("open", $"g={{diagGeneration}} done duration={{{D}.N(Duration)}} {{DiagnosticsState()}}");
""")
e(C, "after", """        internal void Close()
        {
""", f"""            {D}.Log("close", $"control Close {{DiagnosticsState()}}");
""")
e(C, "after", """        internal void CloseAndDisposePlayer()
        {
""", f"""            {D}.Log("close", $"CloseAndDisposePlayer {{DiagnosticsState()}}");
""")
e(C, "after", """            _buttonPlay.Click += (_, _) =>
            {
""", f"""                {D}.Log("play", $"play button click wasPlaying={{_videoPlayerInstance.IsPlaying}} {{DiagnosticsState()}}");
""")

# ---------------------------------------------------------------- MainViewModel
e(M, "after", "        LibMpvDynamicPlayer.MpvPath = Se.DataFolder;\n", f"""        {D}.Folder = Se.DataFolder;
        {D}.Log("session", $"SE {{Se.Version}} engine={{Se.Settings.Video.VideoPlayer}} customLibMpvPath={{!string.IsNullOrEmpty(Se.Settings.General.LibMpvPath)}} libmpvInSeFolder={{(Se.Settings.General.LibMpvPath ?? string.Empty).Contains(Path.DirectorySeparatorChar + "Subtitle Edit" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)}} undocked={{AreVideoControlsUndocked}}");
""")
e(M, "after", """    private async Task VideoOpenFile(string videoFileName, int desiredAudioTrackId = -1, double startPositionSeconds = 0) // OpenVideoFile
    {
""", f"""        {D}.Log("vm", $"VideoOpenFile media={{{D}.MediaTag(videoFileName)}} control={{GetVideoPlayerControl()?.DiagnosticsId}} undocked={{AreVideoControlsUndocked}} fullscreen={{_fullScreenVideoPlayerControl != null}}");
""")
e(M, "after", """    private void VideoCloseFile()
    {
""", f"""        {D}.Log("vm", $"VideoCloseFile current={{{D}.MediaTag(_videoFileName)}} control={{GetVideoPlayerControl()?.DiagnosticsId}}");
""")
e(M, "after", """        int desiredAudioTrackId = -1,
        long videoOffsetToRestoreMs = 0)
    {
""", f"""        {D}.Log("vm", $"SubtitleOpen ext={{Path.GetExtension(fileName)}} givenVideo={{{D}.MediaTag(videoFileName)}} skipLoadVideo={{skipLoadVideo}} current={{{D}.MediaTag(_videoFileName)}}");
""")
e(M, "after", """    private void TogglePlayPause()
    {
        var control = GetVideoPlayerControl();
""", f"""        {D}.Log("play", $"TogglePlayPause control={{control?.DiagnosticsId}} disposed={{control?.IsDisposed}} video={{{D}.MediaTag(_videoFileName)}}");
""")

for ed in edits:
    if ed["file"] == C:
        ed["text"] = ed["text"].replace("Interlocked.", "System.Threading.Interlocked.")

here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "player-diagnostics.patch.json"), "w", encoding="utf-8", newline="\n") as f:
    json.dump({"edits": edits}, f, ensure_ascii=False, indent=1)
print(f"{len(edits)} edits")

