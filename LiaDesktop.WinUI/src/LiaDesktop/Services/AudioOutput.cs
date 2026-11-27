using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LiaDesktop.Services;

/// <summary>
/// Resolve TTS playback to default MULTIMEDIA/CONSOLE render device (never Communications),
/// optional user device id, follow default-device changes, WasapiOut → WaveOutEvent fallback.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    readonly AppLog _log;
    readonly object _gate = new();
    MMDeviceEnumerator? _enum;
    NotificationClient? _notify;
    string? _preferredDeviceId; // empty = system default multimedia
    string _lastDeviceId = "";
    string _lastFriendly = "(none)";
    Role _lastRole = Role.Multimedia;
    bool _defaultDirty;

    /// <summary>Selftest override: force device id / friendly / role without touching real endpoints.</summary>
    public static Func<(string Id, string Friendly, Role Role)>? ResolveOverride { get; set; }

    public string LastDeviceFriendlyName
    {
        get { lock (_gate) return _lastFriendly; }
    }

    public string LastDeviceId
    {
        get { lock (_gate) return _lastDeviceId; }
    }

    public Role LastRole
    {
        get { lock (_gate) return _lastRole; }
    }

    public AudioOutput(AppLog log)
    {
        _log = log;
        try
        {
            _enum = new MMDeviceEnumerator();
            _notify = new NotificationClient(this);
            _enum.RegisterEndpointNotificationCallback(_notify);
        }
        catch (Exception ex)
        {
            _log.Info("AudioOutput notify: " + ex.Message);
        }
    }

    public void SetPreferredDeviceId(string? id)
    {
        lock (_gate)
        {
            _preferredDeviceId = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            _defaultDirty = true;
        }
    }

    public static IReadOnlyList<(string Id, string Name)> ListRenderDevices()
    {
        var list = new List<(string, string)> { ("", "Системное по умолчанию") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add((d.ID, d.FriendlyName)); }
                catch { /* */ }
            }
        }
        catch { /* */ }
        return list;
    }

    public static string? DefaultMultimediaId()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID;
            }
            catch { return null; }
        }
    }

    public static string? DefaultCommunicationsId()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications).ID;
        }
        catch { return null; }
    }

    /// <summary>Open player on Multimedia (or preferred). Never uses Role.Communications.</summary>
    public IWavePlayer OpenPlayer(out string friendlyName, out Role role, out string deviceId, out bool waveOutFallback)
    {
        friendlyName = "";
        role = Role.Multimedia;
        deviceId = "";
        waveOutFallback = false;

        if (ResolveOverride != null)
        {
            var o = ResolveOverride();
            deviceId = o.Id;
            friendlyName = o.Friendly;
            role = o.Role;
            lock (_gate)
            {
                _lastDeviceId = deviceId;
                _lastFriendly = friendlyName;
                _lastRole = role;
                _defaultDirty = false;
            }
            // Selftest override still returns a real WaveOutEvent so Init/Play work with tap
            waveOutFallback = true;
            return new WaveOutEvent { DesiredLatency = 100 };
        }

        MMDevice? device = null;
        try
        {
            _enum ??= new MMDeviceEnumerator();
            string? pref;
            lock (_gate) pref = _preferredDeviceId;

            if (!string.IsNullOrEmpty(pref))
            {
                try
                {
                    device = _enum.GetDevice(pref);
                    if (device.State != DeviceState.Active)
                    {
                        _log.Info($"AudioOutput: preferred inactive ({device.State}) → default multimedia");
                        device.Dispose();
                        device = null;
                    }
                }
                catch (Exception ex)
                {
                    _log.Info("AudioOutput preferred: " + ex.Message);
                    device = null;
                }
            }

            if (device == null)
            {
                // NEVER Communications — headphones/TV users often have a dead Comm endpoint
                try
                {
                    device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    role = Role.Multimedia;
                }
                catch
                {
                    device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    role = Role.Console;
                }
            }
            else
            {
                role = Role.Multimedia; // user pick still treated as media path
            }

            if (device.State != DeviceState.Active)
                throw new InvalidOperationException($"render device state={device.State}");

            deviceId = device.ID;
            friendlyName = device.FriendlyName;
            lock (_gate)
            {
                _lastDeviceId = deviceId;
                _lastFriendly = friendlyName;
                _lastRole = role;
                _defaultDirty = false;
            }

            // Never touch device.AudioClient before WasapiOut — peeking MixFormat activates the
            // client and breaks the subsequent WasapiOut on some endpoints (CI CABLE Input).
            try
            {
                var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
                // WasapiOut takes ownership of device in some NAudio versions — don't dispose device
                device = null;
                return wasapi;
            }
            catch (Exception ex)
            {
                // Includes >2ch virtual mixes that reject stereo shared-mode init
                _log.Info("WasapiOut fail → stereo WaveOutEvent: " + ex.Message);
                waveOutFallback = true;
                try { device?.Dispose(); } catch { /* */ }
                device = null;
                return CreateStereoWaveOut(out friendlyName, out deviceId);
            }
        }
        catch (Exception ex)
        {
            _log.Info("AudioOutput Wasapi resolve fail → stereo WaveOutEvent: " + ex.Message);
            try { device?.Dispose(); } catch { /* */ }
            waveOutFallback = true;
            role = Role.Multimedia;
            var player = CreateStereoWaveOut(out friendlyName, out deviceId);
            lock (_gate)
            {
                _lastDeviceId = deviceId;
                _lastFriendly = friendlyName;
                _lastRole = role;
                _defaultDirty = false;
            }
            return player;
        }
    }

    /// <summary>
    /// WaveOut on a stereo endpoint (never rely on a 16ch WASAPI mix). Used when WasapiOut
    /// fails or the default mix format has &gt;2 channels.
    /// </summary>
    public WaveOutEvent CreateStereoWaveOut(out string friendlyName, out string deviceId)
    {
        var wo = new WaveOutEvent { DesiredLatency = 100 };
        friendlyName = "WAVE_MAPPER (WaveOutEvent stereo)";
        deviceId = "wave_mapper";
        try
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                if (caps.Channels < 2) continue;
                wo.DeviceNumber = i;
                friendlyName = $"WaveOut[{i}] {caps.ProductName}";
                deviceId = $"wave_out:{i}";
                _log.Info($"AudioOutput WaveOut stereo → {friendlyName} ch={caps.Channels}");
                lock (_gate)
                {
                    _lastDeviceId = deviceId;
                    _lastFriendly = friendlyName;
                    _lastRole = Role.Multimedia;
                    _defaultDirty = false;
                }
                return wo;
            }
        }
        catch (Exception ex)
        {
            _log.Info("WaveOut enum: " + ex.Message);
        }
        wo.DeviceNumber = -1;
        lock (_gate)
        {
            _lastDeviceId = deviceId;
            _lastFriendly = friendlyName;
            _lastRole = Role.Multimedia;
            _defaultDirty = false;
        }
        return wo;
    }

    public void MarkDefaultDirty()
    {
        lock (_gate) _defaultDirty = true;
    }

    public bool DefaultDirty
    {
        get { lock (_gate) return _defaultDirty; }
    }

    public void Dispose()
    {
        try
        {
            if (_enum != null && _notify != null)
                _enum.UnregisterEndpointNotificationCallback(_notify);
        }
        catch { /* */ }
        _enum?.Dispose();
        _enum = null;
        _notify = null;
    }

    sealed class NotificationClient : IMMNotificationClient
    {
        readonly AudioOutput _owner;
        public NotificationClient(AudioOutput owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnDeviceRemoved(string deviceId) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render) return;
            // Follow multimedia/console default; ignore communications-only flips
            if (role is Role.Multimedia or Role.Console)
                _owner.MarkDefaultDirty();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
