using System;
using System.Runtime.InteropServices;

namespace Kairos
{
    /// <summary>
    /// Writes an MP4 video (H.264 picture, AAC sound) using the encoders built into Windows (Media Foundation), so no
    /// extra libraries are needed. Frames are BGRA, top row first; sound is 16-bit stereo at 48 kHz.
    /// </summary>
    public sealed class Mp4Writer : IDisposable
    {
        private readonly IMFSinkWriter _writer;
        private readonly int _width, _height, _fps;
        private readonly int _videoStream = -1, _audioStream = -1;
        private long _frames, _audioFrames;
        private bool _finished, _started;

        private const int AudioRate = AudioRecorder.Rate;

        public Mp4Writer(string path, int width, int height, int fps, bool withAudio)
        {
            if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("H.264 needs an even width and height");
            _width = width; _height = height; _fps = fps;

            MFStartup(MF_VERSION, 0);
            _started = true;
            try
            {
                MFCreateAttributes(out var attributes, 2);
                attributes.SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1); // use the graphics card's encoder when there is one
                attributes.SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, 1);
                MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out _writer);

                // ---- Video: H.264 out, 32-bit BGRA in ----
                MFCreateMediaType(out var videoOut);
                videoOut.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                videoOut.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
                videoOut.SetUINT32(MF_MT_AVG_BITRATE, (uint)Math.Clamp((long)width * height * fps / 3, 2_000_000, 16_000_000));
                videoOut.SetUINT32(MF_MT_INTERLACE_MODE, 2); // progressive
                videoOut.SetUINT64(MF_MT_FRAME_SIZE, Pack(width, height));
                videoOut.SetUINT64(MF_MT_FRAME_RATE, Pack(fps, 1));
                videoOut.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                _writer.AddStream(videoOut, out int videoIndex);
                _videoStream = videoIndex;

                MFCreateMediaType(out var videoIn);
                videoIn.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                videoIn.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                videoIn.SetUINT32(MF_MT_INTERLACE_MODE, 2);
                videoIn.SetUINT64(MF_MT_FRAME_SIZE, Pack(width, height));
                videoIn.SetUINT64(MF_MT_FRAME_RATE, Pack(fps, 1));
                videoIn.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                videoIn.SetUINT32(MF_MT_DEFAULT_STRIDE, (uint)(width * 4)); // positive: top row first
                _writer.SetInputMediaType(_videoStream, videoIn, null);

                if (withAudio)
                {
                    // ---- Sound: AAC out (192 kbps), 16-bit stereo PCM in ----
                    MFCreateMediaType(out var audioOut);
                    audioOut.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                    audioOut.SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
                    audioOut.SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    audioOut.SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
                    audioOut.SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
                    audioOut.SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
                    _writer.AddStream(audioOut, out int audioIndex);
                    _audioStream = audioIndex;

                    MFCreateMediaType(out var audioIn);
                    audioIn.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                    audioIn.SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                    audioIn.SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    audioIn.SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
                    audioIn.SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
                    audioIn.SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
                    audioIn.SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AudioRate * 4);
                    _writer.SetInputMediaType(_audioStream, audioIn, null);
                }

                _writer.BeginWriting();
            }
            catch (COMException ex)
            {
                Shutdown();
                throw new InvalidOperationException(
                    "Windows couldn't start its video encoder (on Windows \"N\" editions, install the Media Feature Pack). " + ex.Message, ex);
            }
        }

        private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

        /// <summary>Adds the next frame (BGRA, width × height, top row first).</summary>
        public void AddFrame(byte[] bgra)
        {
            long start = _frames * 10_000_000L / _fps;
            long end = (_frames + 1) * 10_000_000L / _fps;
            Write(_videoStream, bgra, bgra.Length, start, end - start);
            _frames++;
        }

        /// <summary>Adds the next stretch of sound (interleaved 16-bit stereo, 48 kHz).</summary>
        public void AddAudio(short[] interleaved)
        {
            if (_audioStream < 0 || interleaved.Length == 0) return;
            var bytes = new byte[interleaved.Length * 2];
            Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
            long frames = interleaved.Length / 2;
            long start = _audioFrames * 10_000_000L / AudioRate;
            long end = (_audioFrames + frames) * 10_000_000L / AudioRate;
            Write(_audioStream, bytes, bytes.Length, start, end - start);
            _audioFrames += frames;
        }

        private void Write(int stream, byte[] data, int length, long time, long duration)
        {
            MFCreateMemoryBuffer(length, out var buffer);
            try
            {
                buffer.Lock(out IntPtr target, out _, out _);
                try { Marshal.Copy(data, 0, target, length); }
                finally { buffer.Unlock(); }
                buffer.SetCurrentLength(length);

                MFCreateSample(out var sample);
                try
                {
                    sample.AddBuffer(buffer);
                    sample.SetSampleTime(time);
                    sample.SetSampleDuration(duration);
                    _writer.WriteSample(stream, sample);
                }
                finally { Marshal.ReleaseComObject(sample); }
            }
            finally { Marshal.ReleaseComObject(buffer); }
        }

        /// <summary>Finishes the file (writes its index). Without this the MP4 won't play.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            try { _writer.FinalizeWriting(); }
            finally { Shutdown(); }
        }

        private void Shutdown()
        {
            if (_writer != null) { try { Marshal.ReleaseComObject(_writer); } catch { } }
            if (_started) { _started = false; try { MFShutdown(); } catch { } }
        }

        public void Dispose()
        {
            if (!_finished) { _finished = true; Shutdown(); }
        }

        // ----- Media Foundation declarations (mfapi.h, mfidl.h, mfreadwrite.h). Methods in their exact order;
        //       base interface methods are repeated first, as COM requires. Unused slots keep their place. -----

        private const int MF_VERSION = 0x00020070;

        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
        private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFCreateAttributes(out IMFAttributes attributes, int initialSize);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem();
            void DeleteAllItems();
            void SetUINT32(in Guid key, uint value);
            void SetUINT64(in Guid key, ulong value);
            void SetDouble();
            void SetGUID(in Guid key, in Guid value);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount();
            void GetItemByIndex(); void CopyAllItems();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem();
            void DeleteAllItems();
            void SetUINT32(in Guid key, uint value);
            void SetUINT64(in Guid key, ulong value);
            void SetDouble();
            void SetGUID(in Guid key, in Guid value);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount();
            void GetItemByIndex(); void CopyAllItems();
            // IMFMediaType
            void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem();
            void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString();
            void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex();
            void CopyAllItems();
            // IMFSample
            void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime();
            void SetSampleTime(long time);
            void GetSampleDuration();
            void SetSampleDuration(long duration);
            void GetBufferCount(); void GetBufferByIndex(); void ConvertToContiguousBuffer();
            void AddBuffer(IMFMediaBuffer buffer);
            void RemoveBufferByIndex(); void RemoveAllBuffers(); void GetTotalLength(); void CopyToBuffer();
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            void Unlock();
            void GetCurrentLength(out int length);
            void SetCurrentLength(int length);
            void GetMaxLength(out int length);
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            void AddStream(IMFMediaType targetType, out int streamIndex);
            void SetInputMediaType(int streamIndex, IMFMediaType inputType, IMFAttributes? encodingParameters);
            void BeginWriting();
            void WriteSample(int streamIndex, IMFSample sample);
            void SendStreamTick(int streamIndex, long timestamp);
            void PlaceMarker(int streamIndex, IntPtr context);
            void NotifyEndOfSegment(int streamIndex);
            void Flush(int streamIndex);
            void FinalizeWriting();
            void GetServiceForStream();
            void GetStatistics();
        }
    }
}
