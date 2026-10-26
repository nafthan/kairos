using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Kairos
{
    /// <summary>
    /// Records what Spotify is playing, through Windows' own audio system (WASAPI loopback; no extra libraries).
    /// It records Spotify alone where Windows allows that (Windows 10 version 2004 and later); otherwise everything
    /// coming out of the speakers. Audio is kept as 16-bit stereo at 48 kHz.
    /// </summary>
    public sealed class AudioRecorder : IDisposable
    {
        public const int Rate = 48000;
        public const int Channels = 2;

        /// <summary>True if only Spotify is being recorded (false: all sound).</summary>
        public bool SpotifyOnly { get; private set; }
        /// <summary>Set if recording stopped with a problem part-way.</summary>
        public string? Error { get; private set; }

        private readonly object _lock = new();
        private short[] _samples = new short[Rate * Channels * 20];
        private long _frames;
        private readonly int? _processId;
        private Thread? _thread;
        private volatile bool _stop;
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private AudioRecorder(int? processId) => _processId = processId;

        /// <summary>Starts recording: Spotify only if <paramref name="processId"/> is given and Windows supports it.</summary>
        public static async Task<AudioRecorder> StartAsync(int? processId)
        {
            var r = new AudioRecorder(processId);
            r._thread = new Thread(r.Run) { IsBackground = true, Name = "Kairos audio recorder" };
            r._thread.SetApartmentState(ApartmentState.MTA);
            r._thread.Start();
            await r._started.Task;
            return r;
        }

        /// <summary>The main Spotify process (the others are its helpers, recorded along with it).</summary>
        public static int? FindSpotifyProcessId()
        {
            var all = Process.GetProcessesByName("Spotify");
            try
            {
                return all.Select(p =>
                {
                    try { return (Id: p.Id, Started: p.StartTime); }
                    catch { return (Id: p.Id, Started: DateTime.MaxValue); }
                }).OrderBy(p => p.Started).Select(p => (int?)p.Id).FirstOrDefault();
            }
            finally { foreach (var p in all) p.Dispose(); }
        }

        /// <summary>How many sample frames (one per channel pair) have been recorded so far.</summary>
        public long FramesRecorded { get { lock (_lock) return _frames; } }

        /// <summary>A stretch of the recording as interleaved 16-bit stereo (silence where nothing was recorded).</summary>
        public short[] Take(long fromFrame, long frameCount)
        {
            var result = new short[Math.Max(0, frameCount) * Channels];
            lock (_lock)
            {
                long from = Math.Max(0, fromFrame);
                long available = Math.Max(0, Math.Min(frameCount - (from - fromFrame), _frames - from));
                if (available > 0)
                    Array.Copy(_samples, from * Channels, result, (from - fromFrame) * Channels, available * Channels);
            }
            return result;
        }

        public void Stop()
        {
            _stop = true;
            _thread?.Join(3000);
        }

        public void Dispose() => Stop();

        // ----- Recording -----

        private void Append(ReadOnlySpan<short> interleaved)
        {
            lock (_lock)
            {
                long need = (_frames * Channels) + interleaved.Length;
                if (need > _samples.Length)
                {
                    long size = Math.Max(need, (long)_samples.Length * 2);
                    Array.Resize(ref _samples, (int)Math.Min(size, int.MaxValue - 64));
                }
                interleaved.CopyTo(_samples.AsSpan((int)(_frames * Channels)));
                _frames += interleaved.Length / Channels;
            }
        }

        private void Run()
        {
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            AutoResetEvent? ready = null;
            IntPtr mixFormat = IntPtr.Zero, ownFormat = IntPtr.Zero;
            bool started = false;
            try
            {
                SourceFormat format;
                bool spotifyOnly = false;
                if (_processId is int pid)
                {
                    try { client = ActivateProcessLoopback(pid); spotifyOnly = true; }
                    catch { client = null; } // older Windows: record all sound instead
                }

                if (spotifyOnly && client != null)
                {
                    // Windows converts Spotify's sound to exactly the format asked for.
                    format = new SourceFormat(Rate, Channels, 16, IsFloat: false);
                    ownFormat = Marshal.AllocCoTaskMem(18);
                    WriteWaveFormat(ownFormat, Rate, Channels, 16);
                    client.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, 2_000_000, 0, ownFormat, IntPtr.Zero);
                    ready = new AutoResetEvent(false);
                    client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                }
                else
                {
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    enumerator.GetDefaultAudioEndpoint(0 /* render */, 0 /* console */, out var device);
                    var iid = typeof(IAudioClient).GUID;
                    device.Activate(ref iid, 0x17, IntPtr.Zero, out var obj);
                    client = (IAudioClient)obj;
                    client.GetMixFormat(out mixFormat);
                    format = ReadWaveFormat(mixFormat);
                    client.Initialize(0, LOOPBACK, 2_000_000, 0, mixFormat, IntPtr.Zero);
                }

                var captureIid = typeof(IAudioCaptureClient).GUID;
                client.GetService(ref captureIid, out var service);
                capture = (IAudioCaptureClient)service;
                client.Start();
                SpotifyOnly = spotifyOnly;
                started = true;
                _started.TrySetResult(true);

                var converter = new Converter(format);
                var clock = Stopwatch.StartNew();
                double lastPacket = 0;
                while (!_stop)
                {
                    if (ready != null) ready.WaitOne(50); else Thread.Sleep(10);

                    capture.GetNextPacketSize(out uint packet);
                    while (packet > 0)
                    {
                        capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                        Append(converter.Convert(data, (int)frames, (flags & SILENT) != 0));
                        capture.ReleaseBuffer(frames);
                        lastPacket = clock.Elapsed.TotalSeconds;
                        capture.GetNextPacketSize(out packet);
                    }

                    // Windows sends nothing at all while nothing is playing: fill those gaps with silence so the
                    // recording keeps time with the clock.
                    double now = clock.Elapsed.TotalSeconds;
                    if (now - lastPacket > 0.15)
                    {
                        long behind = (long)((now - 0.05) * Rate) - FramesRecorded;
                        if (behind > Rate / 20) Append(new short[behind * Channels]);
                    }
                }
                client.Stop();
            }
            catch (Exception ex)
            {
                if (!started) _started.TrySetException(new InvalidOperationException("Couldn't record sound: " + ex.Message, ex));
                else Error = ex.Message;
            }
            finally
            {
                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
                if (ownFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(ownFormat);
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
                ready?.Dispose();
            }
        }

        // ----- Formats -----

        internal readonly record struct SourceFormat(int Rate, int Channels, int Bits, bool IsFloat);

        private static void WriteWaveFormat(IntPtr p, int rate, int channels, int bits)
        {
            Marshal.WriteInt16(p, 0, 1);                                  // PCM
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * channels * bits / 8);        // bytes per second
            Marshal.WriteInt16(p, 12, (short)(channels * bits / 8));     // block align
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 0);                                 // no extra bytes
        }

        private static SourceFormat ReadWaveFormat(IntPtr p)
        {
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            int channels = (ushort)Marshal.ReadInt16(p, 2);
            int rate = Marshal.ReadInt32(p, 4);
            int bits = (ushort)Marshal.ReadInt16(p, 14);
            bool isFloat = tag == 3;
            if (tag == 0xFFFE) // WAVEFORMATEXTENSIBLE: the real type is in the sub-format
            {
                var sub = Marshal.PtrToStructure<Guid>(p + 24);
                isFloat = sub == new Guid("00000003-0000-0010-8000-00aa00389b71");
            }
            return new SourceFormat(rate, Math.Max(1, channels), bits, isFloat);
        }

        /// <summary>Turns Windows' audio (any rate, float or integer, any channels) into 16-bit stereo at 48 kHz.</summary>
        internal sealed class Converter
        {
            private readonly SourceFormat _f;
            private readonly double _step;
            private double _pos;
            private double _prevL, _prevR;

            public Converter(SourceFormat format)
            {
                _f = format;
                _step = (double)format.Rate / Rate;
            }

            public short[] Convert(IntPtr data, int frames, bool silent)
            {
                int bytes = _f.Bits / 8, frameBytes = bytes * _f.Channels;
                var raw = new byte[frames * frameBytes];
                if (!silent && data != IntPtr.Zero && raw.Length > 0) Marshal.Copy(data, raw, 0, raw.Length);
                return Convert(raw, frames, silent);
            }

            public short[] Convert(byte[] raw, int frames, bool silent)
            {
                int bytes = _f.Bits / 8, frameBytes = bytes * _f.Channels;
                var output = new short[(int)Math.Ceiling(frames / _step + 2) * Channels];
                int n = 0;
                for (int i = 0; i < frames; i++)
                {
                    double l = 0, r = 0;
                    if (!silent)
                    {
                        l = Sample(raw, i * frameBytes);
                        r = _f.Channels > 1 ? Sample(raw, i * frameBytes + bytes) : l;
                    }
                    // Linear interpolation between the previous frame and this one, at the output rate.
                    while (_pos < 1)
                    {
                        if (n + 1 >= output.Length) Array.Resize(ref output, output.Length * 2 + 4);
                        output[n++] = ToShort(_prevL + (l - _prevL) * _pos);
                        output[n++] = ToShort(_prevR + (r - _prevR) * _pos);
                        _pos += _step;
                    }
                    _pos -= 1;
                    _prevL = l; _prevR = r;
                }
                Array.Resize(ref output, n);
                return output;
            }

            private double Sample(byte[] raw, int at)
            {
                if (_f.IsFloat)
                    return _f.Bits == 64 ? BitConverter.ToDouble(raw, at) : BitConverter.ToSingle(raw, at);
                return _f.Bits switch
                {
                    8 => (raw[at] - 128) / 128.0,
                    16 => BitConverter.ToInt16(raw, at) / 32768.0,
                    24 => ((raw[at] | (raw[at + 1] << 8) | ((sbyte)raw[at + 2] << 16))) / 8388608.0,
                    32 => BitConverter.ToInt32(raw, at) / 2147483648.0,
                    _ => 0,
                };
            }

            private static short ToShort(double v) => (short)Math.Clamp(Math.Round(v * 32767), -32768, 32767);
        }

        // ----- Spotify only (process loopback) -----

        private static IAudioClient ActivateProcessLoopback(int processId)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS: process loopback, this process and its children.
            IntPtr parameters = Marshal.AllocHGlobal(12);
            IntPtr variant = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(parameters, 0, 1);          // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(parameters, 4, processId);  // target process
                Marshal.WriteInt32(parameters, 8, 0);          // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(variant, i, 0);
                Marshal.WriteInt16(variant, 0, 65);            // VT_BLOB
                Marshal.WriteInt32(variant, 8, 12);            // blob size
                Marshal.WriteIntPtr(variant, 16, parameters);  // blob data

                var handler = new ActivationHandler();
                var iid = typeof(IAudioClient).GUID;
                ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, handler, out var operation);
                if (!handler.Done.WaitOne(5000)) throw new TimeoutException("Windows didn't respond");
                operation.GetActivateResult(out int hr, out object instance);
                Marshal.ThrowExceptionForHR(hr);
                return (IAudioClient)instance;
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(parameters);
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new(false);
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
        }

        // ----- Windows audio declarations (audioclient.h, mmdeviceapi.h). Methods in their exact order. -----

        private const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
        private const uint SILENT = 0x2;

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        /// <summary>Marks the handler as usable from any thread, which Windows requires for the callback.</summary>
        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAgileObject { }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioClient
        {
            void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            void GetBufferSize(out uint frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr eventHandle);
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioCaptureClient
        {
            void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            void ReleaseBuffer(uint frames);
            void GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }
    }
}
