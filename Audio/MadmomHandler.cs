﻿using Spectrum.Base;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Spectrum.Audio {

  public class MadmomHandler {

    private readonly Configuration config;
    private readonly AudioInput audio;

    private Process process;
    private double lastTimestamp = 0.0;

    public MadmomHandler(Configuration config, AudioInput audio) {
      this.config = config;
      this.audio = audio;
      this.config.PropertyChanged += ConfigUpdated;
    }

    private void ConfigUpdated(object sender, PropertyChangedEventArgs e) {
      if (e.PropertyName == "audioDeviceID" || e.PropertyName == "beatInput") {
        this.UpdateEnabled();
      }
    }

    private bool active;
    public bool Active {
      get {
        return this.active;
      }
      set {
        if (this.active == value) {
          return;
        }
        this.active = value;
        this.UpdateEnabled();
      }
    }

    private void UpdateEnabled() {
      if (this.process != null) {
        this.process.Kill();
        this.process.Dispose();
        this.process = null;
      }

      if (!this.active || this.config.beatInput != 1) {
        return;
      }

      var currentDir = Directory.GetParent(Environment.CurrentDirectory);
      var rootDir = currentDir.Parent.Parent.FullName;
      var envScriptPath = Path.Combine(rootDir, "Madmom", "env", "Scripts");

      ProcessStartInfo start = new ProcessStartInfo();
      start.WorkingDirectory = envScriptPath;
      start.FileName = "python.exe";
      start.Arguments = string.Format(
        "DBNBeatTracker --audio_input={0} online",
        this.audio.CurrentAudioDeviceIndex
      );
      start.UseShellExecute = false;
      start.RedirectStandardOutput = true;
      start.CreateNoWindow = true;

      this.process = Process.Start(start);
      this.process.OutputDataReceived += BeatDetected;
      this.process.BeginOutputReadLine();
    }

    private void BeatDetected(object sender, DataReceivedEventArgs e) {
      string line = e.Data;
      if (line == null || !line.StartsWith("BEAT:")) {
        return;
      }

      double timestamp = Convert.ToDouble(line.Substring(5));
      int millisecondsSinceLast = (int)((timestamp - this.lastTimestamp) * 1000);
      this.lastTimestamp = timestamp;

      this.config.beatBroadcaster.ReportMadmomBeat(millisecondsSinceLast);
    }

  }

}
