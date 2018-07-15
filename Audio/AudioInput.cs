﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Spectrum.Base;
using System.Diagnostics;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Dsp;

namespace Spectrum.Audio {

  public enum AudioDetectorType : byte { Kick, Snare }

  public class AudioEvent {
    public AudioDetectorType type;
    public double significance;
  }

  public class AudioInput : Input {

    private static Dictionary<AudioDetectorType, double[]> bins =
      new Dictionary<AudioDetectorType, double[]>() {
        { AudioDetectorType.Kick, new double[] { 40, 50 } },
        { AudioDetectorType.Snare, new double[] { 1500, 2500 } },
      };

    private static bool WindowContains(double[] window, int index) {
      return (FreqToFFTBin(window[0]) <= index
        && FreqToFFTBin(window[1]) >= index);
    }

    private static int FreqToFFTBin(double freq) {
      return (int)(freq / 2.69);
    }

    private Configuration config;

    private WasapiCapture recordingDevice;
    private List<short> unanalyzedValues = new List<short>();

    // These values get continuously updated by the internal thread
    public static readonly int fftSize = 8192;
    public float[] AudioData { get; private set; } = new float[fftSize];
    private ConcurrentDictionary<string, double> maxAudioDataLevels = new ConcurrentDictionary<string, double>();
    public float Volume { get; private set; } = 0.0f;

    // We loop around the history array based on this offset
    private int currentHistoryOffset = 0;

    public double BPM { get; private set; } = 0.0;

    private static int historyLength = 32;
    private Dictionary<AudioDetectorType, double[]> energyHistory;
    private ConcurrentDictionary<AudioDetectorType, AudioEvent> eventBuffer;
    private List<AudioEvent> eventsSinceLastTick;
    private Dictionary<AudioDetectorType, long> lastEventTime;

    public AudioInput(Configuration config) {
      this.config = config;

      this.energyHistory = new Dictionary<AudioDetectorType, double[]>();
      this.eventBuffer =
        new ConcurrentDictionary<AudioDetectorType, AudioEvent>();
      this.eventsSinceLastTick = new List<AudioEvent>();
      this.lastEventTime = new Dictionary<AudioDetectorType, long>();
      foreach (var key in bins.Keys) {
        this.energyHistory[key] = new double[historyLength];
        this.lastEventTime[key] = 0;
      }
    }

    /**
     * Strange incantations required to make the Un4seen libraries work are
     * quarantined here.
     */
    private bool active;
    public bool Active {
      get {
        lock (this.maxAudioDataLevels) {
          return this.active;
        }
      }
      set {
        lock (this.maxAudioDataLevels) {
          if (this.active == value) {
            return;
          }
          if (value) {
            this.InitializeAudio();
          } else {
            this.TerminateAudio();
          }
          this.active = value;
        }
      }
    }

    public bool AlwaysActive {
      get {
        return false;
      }
    }

    public bool Enabled {
      get {
        return true;
      }
    }

    private void InitializeAudio() {
      if (this.config.audioDeviceID == null) {
        throw new Exception("audioDeviceID not set!");
      }
      var iterator = new MMDeviceEnumerator().EnumerateAudioEndPoints(
        DataFlow.Capture,
        DeviceState.Active
      );
      MMDevice device = null;
      foreach (var audioDevice in iterator) {
        if (this.config.audioDeviceID == audioDevice.ID) {
          device = audioDevice;
          break;
        }
      }
      if (device == null) {
        throw new Exception("audioDeviceID not set!");
      }
      this.recordingDevice = new WasapiCapture(device, false, 16);
      this.recordingDevice.DataAvailable += Update;
      this.recordingDevice.StartRecording();
    }

    private void TerminateAudio() {
      this.recordingDevice.StopRecording();
      this.recordingDevice = null;
    }

    private void Update(object sender, NAudio.Wave.WaveInEventArgs args) {
      lock (this.maxAudioDataLevels) {
        short[] values = new short[args.Buffer.Length / 2];
        for (int i = 0; i < args.BytesRecorded; i += 2) {
          values[i / 2] = (short)((args.Buffer[i + 1] << 8) | args.Buffer[i]);
        }
        this.unanalyzedValues.AddRange(values);

        if (this.unanalyzedValues.Count >= fftSize) {
          this.GenerateAudioData();
          this.unanalyzedValues.Clear();
        }

        foreach (var pair in this.config.levelDriverPresets) {
          if (pair.Value.Source != LevelDriverSource.Audio) {
            continue;
          }
          AudioLevelDriverPreset preset = (AudioLevelDriverPreset)pair.Value;
          double filteredMax = AudioInput.GetFilteredMax(
            preset.FilterRangeStart,
            preset.FilterRangeEnd,
            this.AudioData
          );
          if (this.maxAudioDataLevels.ContainsKey(preset.Name)) {
            this.maxAudioDataLevels[preset.Name] = Math.Max(
              this.maxAudioDataLevels[preset.Name],
              filteredMax
            );
          } else {
            this.maxAudioDataLevels[preset.Name] = filteredMax;
          }
        }
        this.UpdateEnergyHistory();
      }
    }

    private void GenerateAudioData() {
      float peakLevel = 0;
      foreach (var value in this.unanalyzedValues) {
        var sampleLevel = value / 32768f;
        if (sampleLevel < 0) sampleLevel = -sampleLevel;
        if (sampleLevel > peakLevel) peakLevel = sampleLevel;
      }
      this.Volume = peakLevel;

      // Q: make sure that fft_data can be 'filled in' by values[]
      // Ideally buffer should have exactly as much data as we need for fft
      // Possibly tweak latency or the thread sleep duration? Alternatively increase FFT reso.
      Complex[] fft_data = new Complex[fftSize];
      int i = 0;
      foreach (var value in this.unanalyzedValues.GetRange(0, fftSize)) {
        fft_data[i].X = (float)(value * FastFourierTransform.BlackmannHarrisWindow(i, fftSize));
        fft_data[i].Y = 0;
        i++;
      }
      FastFourierTransform.FFT(true, (int)Math.Log(fftSize, 2.0), fft_data);

      // FFT results are Complex
      // now we want the magnitude of each band

      float[] fft_results = new float[fftSize];

      for (int j = 0; j < fftSize; j++) {
        fft_results[j] = Magnitude(fft_data[j].X, fft_data[j].Y);
      }
      // fft_results should have 8192 results
      this.AudioData = fft_results;
    }

    private float Magnitude(float x, float y) {
      return (float)Math.Sqrt((float)Math.Pow(x, 2) + (float)Math.Pow(y, 2));
    }

    private void UpdateEnergyHistory() {
      var energyLevels = new Dictionary<AudioDetectorType, double>();
      foreach (var key in bins.Keys) {
        energyLevels[key] = 0.0;
      }
      for (int i = 1; i < this.AudioData.Length / 2; i++) {
        foreach (var pair in bins) {
          AudioDetectorType type = pair.Key;
          double[] window = pair.Value;
          if (WindowContains(window, i)) {
            energyLevels[type] += this.AudioData[i] * this.AudioData[i];
          }
        }
      }

      foreach (var type in bins.Keys) {
        double current = energyLevels[type];
        double[] history = this.energyHistory[type];
        double previous = history[
          (this.currentHistoryOffset + historyLength - 1) % historyLength
        ];
        double change = current - previous;
        double avg = history.Average();
        double max = history.Max();
        double ssd = history.Select(val => (val - avg) * (val - avg)).Sum();
        double sd = Math.Sqrt(ssd / historyLength);
        double stdsFromAverage = (current - avg) / sd;

        if (type == AudioDetectorType.Kick) {
          if (
            current > max &&
            stdsFromAverage > this.config.kickT &&
            avg < this.config.kickQ &&
            current > .001
          ) {
            double significance = Math.Atan(
              stdsFromAverage / (this.config.kickT + 0.001)
            ) * 2 / Math.PI;
            this.UpdateEvent(type, significance);
          }
        } else if (type == AudioDetectorType.Snare) {
          if (
            current > max &&
            stdsFromAverage > this.config.snareT &&
            avg < this.config.snareQ &&
            current > .001
          ) {
            double significance = Math.Atan(
              stdsFromAverage / (this.config.snareT + 0.001)
            ) * 2 / Math.PI;
            this.UpdateEvent(type, significance);
          }
        }
      }

      foreach (var type in bins.Keys) {
        this.energyHistory[type][this.currentHistoryOffset] =
          energyLevels[type];
      }
      this.currentHistoryOffset = (this.currentHistoryOffset + 1)
        % historyLength;
    }

    private void UpdateEvent(AudioDetectorType type, double significance) {
      this.eventBuffer.AddOrUpdate(
        type,
        audioDetectorType => {
          return new AudioEvent() {
            type = audioDetectorType,
            significance = significance,
          };
        },
        (audioDetectorType, existingAudioEvent) => {
          existingAudioEvent.significance = Math.Max(
            existingAudioEvent.significance,
            significance
          );
          return existingAudioEvent;
        }
      );
    }

    public void OperatorUpdate() {
      long timestamp = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
      this.eventsSinceLastTick = new List<AudioEvent>(
        bins.Keys.Select(type => {
          var earliestNextEventTime =
            this.lastEventTime[type] + this.config.domeAutoFlashDelay;
          if (timestamp < earliestNextEventTime) {
            return null;
          }
          AudioEvent audioEvent;
          this.eventBuffer.TryRemove(type, out audioEvent);
          if (audioEvent != null) {
            this.lastEventTime[type] = timestamp;
          }
          return audioEvent;
        }).Where(audioEvent => audioEvent != null)
      );
    }

    public static List<AudioDevice> AudioDevices {
      get {
        var audioDeviceList = new List<AudioDevice>();
        var iterator = new MMDeviceEnumerator().EnumerateAudioEndPoints(
          DataFlow.Capture,
          DeviceState.Active
        );
        foreach (var audioDevice in iterator) {
          audioDeviceList.Add(new AudioDevice() { id = audioDevice.ID, name = audioDevice.FriendlyName });
        }
        return audioDeviceList;
      }
    }

    public List<AudioEvent> GetEventsSinceLastTick() {
      return this.eventsSinceLastTick;
    }

    public double LevelForChannel(int channelIndex) {
      double? midiLevel =
        this.config.beatBroadcaster.CurrentMidiLevelDriverValueForChannel(
          channelIndex
        );
      if (midiLevel.HasValue) {
        return midiLevel.Value;
      }
      string audioPreset =
        this.config.channelToAudioLevelDriverPreset[channelIndex];
      if (
        audioPreset == null ||
        !this.config.levelDriverPresets.ContainsKey(audioPreset) ||
        !(this.config.levelDriverPresets[audioPreset] is AudioLevelDriverPreset)
      ) {
        return 0.0;
      }
      AudioLevelDriverPreset preset =
        (AudioLevelDriverPreset)this.config.levelDriverPresets[audioPreset];
      if (preset.FilterRangeStart == 0.0 && preset.FilterRangeEnd == 1.0) {
        return this.Volume;
      }
      var maxLevel = this.maxAudioDataLevels.ContainsKey(preset.Name)
        ? this.maxAudioDataLevels[preset.Name]
        : 1.0;

      return AudioInput.GetFilteredMax(
        preset.FilterRangeStart,
        preset.FilterRangeEnd,
        this.AudioData
      ) / maxLevel;
    }

    private static double GetFilteredMax(
      double low,
      double high,
      float[] audioData
    ) {
      double lowFreq = AudioInput.EstimateFreq(low, 144.4505);
      int lowBinIndex = (int)AudioInput.GetFrequencyBinUnrounded(lowFreq);
      double highFreq = AudioInput.EstimateFreq(high, 144.4505);
      int highBinIndex = (int)Math.Ceiling(AudioInput.GetFrequencyBinUnrounded(highFreq));
      return audioData.Skip(lowBinIndex).Take(highBinIndex - lowBinIndex + 1).Max();
    }

    // x is a number between 0 and 1
    private static double EstimateFreq(double x, double freqScale) {
      // Changing freq_scale will lower resolution at the lower frequencies in exchange for more coverage of higher frequencies
      // 119.65 corresponds to a tuning that covers the human voice
      // 123.52 corresponds to a tuning that covers the top of a Soprano sax
      // 144.45 corresponds to a tuning that includes the top end of an 88-key piano
      // 150.69 corresponds to a tuning that exceeds a piccolo
      return 15.0 + Math.Exp(.05773259 * freqScale * x);
    }

    private static double GetFrequencyBinUnrounded(double frequency) {
      int streamRate = 44100;
      return fftSize * frequency / streamRate;
    }

  }

}