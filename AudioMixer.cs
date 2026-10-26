using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Kairos
{
    /// <summary>One app that's playing (or has played) sound, as the Windows volume mixer sees it.</summary>
    public sealed class AppAudio
    {
        internal readonly List<ISimpleAudioVolume> Volumes = new();
        internal readonly List<IAudioMeterInformation> Meters = new();

        public string Name { get; init; } = "";
        public string ProcessName { get; init; } = "";
        public bool IsSpotify { get; init; }
        public bool IsSystemSounds { get; init; }

        /// <summary>0 … 1</summary>
        public float Volume
        {
            get { try { Volumes[0].GetMasterVolume(out var v); return v; } catch { return 0; } }
            set
            {
                var ctx = Guid.Empty;
                foreach (var vol in Volumes) try { vol.SetMasterVolume(Math.Clamp(value, 0f, 1f), ref ctx); } catch { }
            }
        }

        public bool Muted
        {
            get { try { Volumes[0].GetMute(out var m); return m; } catch { return false; } }
            set
            {
                var ctx = Guid.Empty;
                foreach (var vol in Volumes) try { vol.SetMute(value, ref ctx); } catch { }
            }
        }

        /// <summary>How loud it is right now, 0 … 1.</summary>
        public float Peak
        {
            get
            {
                float max = 0;
                foreach (var m in Meters) try { m.GetPeakValue(out var p); if (p > max) max = p; } catch { }
                return max;
            }
        }
    }

    /// <summary>
    /// Windows' own audio controls (Core Audio), used directly: master volume, per-app volume and mute,
    /// and live levels. This is what the Windows volume mixer uses.
    /// </summary>
    public static class AudioMixer
    {
        private static IMMDeviceEnumerator? _enumerator;
        private static IMMDevice? _device;
        private static string? _deviceId;
        private static DateTime _deviceChecked;
        private static IAudioMeterInformation? _masterMeter;

        /// <summary>The current speakers/headphones. Re-checked every couple of seconds in case you switch output.</summary>
        private static IMMDevice? DefaultDevice()
        {
            if (_device != null && DateTime.UtcNow - _deviceChecked < TimeSpan.FromSeconds(2)) return _device;
            try
            {
                _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
                device.GetId(out var id);
                if (id != _deviceId)
                {
                    // Output changed (e.g. headphones plugged in): start again with the new one.
                    _deviceId = id;
                    _masterMeter = null;
                }
                _device = device;
                _deviceChecked = DateTime.UtcNow;
                return device;
            }
            catch
            {
                _enumerator = null; // e.g. the audio service restarted: try again fresh next time
                _device = null;
                _masterMeter = null;
                return null;
            }
        }

        private static T? Activate<T>(IMMDevice device) where T : class
        {
            try
            {
                var iid = typeof(T).GUID;
                device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var obj);
                return obj as T;
            }
            catch { return null; }
        }

        /// <summary>Level of everything playing through the speakers, 0 … 1.</summary>
        public static float MasterPeak
        {
            get
            {
                var device = DefaultDevice();
                if (device == null) return 0;
                _masterMeter ??= Activate<IAudioMeterInformation>(device);
                if (_masterMeter == null) return 0;
                try { _masterMeter.GetPeakValue(out var p); return p; }
                catch { _masterMeter = null; return 0; }
            }
        }

        // ----- Apps -----

        /// <summary>Every speaker/headphone output that's switched on (Spotify may not use the default one).</summary>
        private static List<IMMDevice> OutputDevices()
        {
            var list = new List<IMMDevice>();
            try
            {
                _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out var devices);
                devices.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    try { devices.Item(i, out var d); list.Add(d); } catch { }
                }
            }
            catch
            {
                _enumerator = null;
            }
            if (list.Count == 0)
            {
                var fallback = DefaultDevice();
                if (fallback != null) list.Add(fallback);
            }
            return list;
        }

        private const int DEVICE_STATE_ACTIVE = 0x1;

        /// <summary>Every app with sound on any output, grouped by app (Spotify, browsers, games…).</summary>
        public static List<AppAudio> GetApps()
        {
            var result = new List<AppAudio>();
            var byApp = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
            _lastDeviceCount = 0;

            foreach (var device in OutputDevices())
            {
                var manager = Activate<IAudioSessionManager2>(device);
                if (manager == null) continue;
                _lastDeviceCount++;

                try
                {
                    manager.GetSessionEnumerator(out var sessions);
                    sessions.GetCount(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        try { AddSession(sessions, i, byApp, result); }
                        catch { /* skip a session that went away mid-way */ }
                    }
                }
                catch { }
            }

            // Spotify first, system sounds last, the rest alphabetically.
            return result.Where(a => a.Volumes.Count > 0)
                         .OrderByDescending(a => a.IsSpotify)
                         .ThenBy(a => a.IsSystemSounds)
                         .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        private static void AddSession(IAudioSessionEnumerator sessions, int index, Dictionary<string, AppAudio> byApp, List<AppAudio> result)
        {
            sessions.GetSession(index, out var control);
            if (control is not IAudioSessionControl2 c2) return;
            c2.GetState(out var state);
            if (state == AudioMixerSessionState.Expired) return;

            bool system = c2.IsSystemSoundsSession() == 0; // S_OK means yes

            // The session id includes the program's path (e.g. "…\Spotify\Spotify.exe…"): the most reliable way
            // to recognise Spotify, including the Microsoft Store version.
            string sessionId = "";
            try { c2.GetSessionIdentifier(out sessionId); } catch { }
            uint pid = 0;
            try { c2.GetProcessId(out pid); } catch { }

            string processName = "", name = "";
            if (system)
            {
                processName = "System";
                name = "System sounds";
            }
            else
            {
                if (pid != 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        processName = p.ProcessName;
                        try { name = p.MainModule?.FileVersionInfo.FileDescription ?? ""; } catch { /* other users' / protected apps */ }
                    }
                    catch { /* closed, or not ours to look at: fall back to the session id */ }
                }
                if (string.IsNullOrEmpty(processName))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(sessionId, @"([^\\/|%]+)\.exe", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    processName = m.Success ? m.Groups[1].Value : $"app {pid}";
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        c2.GetDisplayName(out var display);
                        name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith("@") ? display : processName;
                    }
                    catch { name = processName; }
                }
            }

            bool spotify = !system &&
                           (processName.Equals("Spotify", StringComparison.OrdinalIgnoreCase) ||
                            sessionId.Contains("spotify", StringComparison.OrdinalIgnoreCase));
            string key = spotify ? "Spotify" : processName;

            if (!byApp.TryGetValue(key, out var app))
            {
                app = new AppAudio { Name = spotify ? "Spotify" : name, ProcessName = key, IsSystemSounds = system, IsSpotify = spotify };
                byApp[key] = app;
                result.Add(app);
            }
            if (control is ISimpleAudioVolume vol) app.Volumes.Add(vol);
            if (control is IAudioMeterInformation meter) app.Meters.Add(meter);
        }

        private static int _lastDeviceCount;

        public static AppAudio? Spotify() => GetApps().FirstOrDefault(a => a.IsSpotify);

        /// <summary>What Kairos can hear, for when Spotify can't be found ("2 outputs: Chrome, Discord").</summary>
        public static string Describe()
        {
            var apps = GetApps();
            string outputs = _lastDeviceCount == 1 ? "1 output" : $"{_lastDeviceCount} outputs";
            return apps.Count == 0
                ? $"Windows reports no apps making sound ({outputs} checked)."
                : $"Sound found on {outputs} from: " + string.Join(", ", apps.Select(a => a.Name));
        }

        // =================================================================
        //  Core Audio COM declarations (from Windows' mmdeviceapi.h / audiopolicy.h / endpointvolume.h).
        //  Each interface lists its base interface's methods first, in order, as COM requires.
        // =================================================================

        private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
        private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

        private static class ClsCtx { public const int All = 0x17; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
            void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
            void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            void RegisterEndpointNotificationCallback(IntPtr client);
            void UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            void GetCount(out uint count);
            void Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            void OpenPropertyStore(int access, out IntPtr properties);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out int state);
        }

    }


    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
        void GetMeteringChannelCount(out uint count);
        void GetChannelsPeakValues(uint count, [MarshalAs(UnmanagedType.LPArray)] float[] peaks);
        void QueryHardwareSupport(out uint mask);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // IAudioSessionManager
        void GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IAudioSessionControl2 control);
        void GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out ISimpleAudioVolume volume);
        // IAudioSessionManager2
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        void RegisterSessionNotification(IntPtr notification);
        void UnregisterSessionNotification(IntPtr notification);
        void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        void UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out AudioMixerSessionState state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        void GetGroupingParam(out Guid grouping);
        void SetGroupingParam(ref Guid grouping, ref Guid context);
        void RegisterAudioSessionNotification(IntPtr notifications);
        void UnregisterAudioSessionNotification(IntPtr notifications);
        // IAudioSessionControl2
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    internal enum AudioMixerSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6F0D7A7F4E8E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid context);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
