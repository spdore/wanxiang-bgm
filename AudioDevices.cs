using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BgmHotkey
{
    internal sealed class AudioDevice
    {
        public uint Id { get; private set; }
        public string Name { get; private set; }

        public AudioDevice(uint id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class AudioDeviceCatalog
    {
        private const uint Mapper = 0xFFFFFFFF;
        private readonly List<AudioDevice> _outputs = new List<AudioDevice>();
        private readonly List<AudioDevice> _microphones = new List<AudioDevice>();
        private readonly List<AudioDevice> _cables = new List<AudioDevice>();
        private readonly List<AudioDevice> _cableRecordings = new List<AudioDevice>();

        public IList<AudioDevice> Outputs { get { return _outputs.AsReadOnly(); } }
        public IList<AudioDevice> Microphones { get { return _microphones.AsReadOnly(); } }
        public IList<AudioDevice> Cables { get { return _cables.AsReadOnly(); } }

        public AudioDeviceCatalog()
        {
            Refresh();
        }

        public void Refresh()
        {
            _outputs.Clear();
            _microphones.Clear();
            _cables.Clear();
            _cableRecordings.Clear();
            _outputs.Add(new AudioDevice(Mapper, "默认系统播放设备"));

            uint outputCount = WinMm.waveOutGetNumDevs();
            for (uint i = 0; i < outputCount; i++)
            {
                WinMm.WaveOutCaps caps;
                if (WinMm.waveOutGetDevCaps(new IntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(WinMm.WaveOutCaps))) != 0)
                    continue;
                string name = (caps.Name ?? "").TrimEnd('\0').Trim();
                AudioDevice device = new AudioDevice(i, name);
                _outputs.Add(device);
                if (IsCableName(name) && name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    name.IndexOf("16ch", StringComparison.OrdinalIgnoreCase) < 0)
                    _cables.Add(device);
            }

            uint inputCount = WinMm.waveInGetNumDevs();
            for (uint i = 0; i < inputCount; i++)
            {
                WinMm.WaveInCaps caps;
                if (WinMm.waveInGetDevCaps(new IntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(WinMm.WaveInCaps))) != 0)
                    continue;
                string name = (caps.Name ?? "").TrimEnd('\0').Trim();
                // CABLE Output is a virtual recording endpoint, not the user's physical microphone.
                if (IsCableName(name))
                    _cableRecordings.Add(new AudioDevice(i, name));
                else if (name.IndexOf("立体声混音", StringComparison.OrdinalIgnoreCase) < 0 &&
                         name.IndexOf("Stereo Mix", StringComparison.OrdinalIgnoreCase) < 0 &&
                         name.IndexOf("What U Hear", StringComparison.OrdinalIgnoreCase) < 0 &&
                         name.IndexOf("Streaming Spea", StringComparison.OrdinalIgnoreCase) < 0)
                    _microphones.Add(new AudioDevice(i, name));
            }
        }

        public AudioDevice FindOutput(string savedName)
        {
            return Find(_outputs, savedName);
        }

        public AudioDevice FindMicrophone(string savedName)
        {
            return FindExactOrDefault(_microphones, savedName);
        }

        public AudioDevice FindCable(string savedName)
        {
            if (String.IsNullOrWhiteSpace(savedName)) return FindCableByDefaultName();
            return FindExactOrDefault(_cables, savedName);
        }

        public AudioDevice FindCableByDefaultName()
        {
            foreach (AudioDevice cable in _cables)
                if (cable.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0)
                    return cable;
            return _cables.Count > 0 ? _cables[0] : null;
        }

        public AudioDevice FindCableRecording(AudioDevice playback)
        {
            if (playback == null) return null;
            string renderName = EndpointName(playback.Name);
            int input = renderName.IndexOf("Input", StringComparison.OrdinalIgnoreCase);
            if (input < 0) return null;
            string expected = renderName.Substring(0, input) + "Output" + renderName.Substring(input + 5);
            foreach (AudioDevice recording in _cableRecordings)
                if (String.Equals(EndpointName(recording.Name), expected, StringComparison.OrdinalIgnoreCase))
                    return recording;
            return null;
        }

        private static string EndpointName(string name)
        {
            int parenthesis = name.IndexOf('(');
            return (parenthesis < 0 ? name : name.Substring(0, parenthesis)).Trim();
        }

        private static AudioDevice FindExactOrDefault(List<AudioDevice> devices, string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return devices.Count > 0 ? devices[0] : null;
            foreach (AudioDevice device in devices)
                if (String.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase)) return device;
            return null;
        }

        private static AudioDevice Find(List<AudioDevice> devices, string name)
        {
            if (!String.IsNullOrWhiteSpace(name))
            {
                foreach (AudioDevice device in devices)
                    if (String.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
                        return device;
            }
            return devices.Count > 0 ? devices[0] : null;
        }

        private static bool IsCableName(string name)
        {
            return name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
