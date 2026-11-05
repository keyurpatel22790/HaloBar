using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HaloBar.Services;

/// <summary>One app making sound: its name, how loud it is, whether it's muted, and the live session.</summary>
internal sealed record MixerApp(string Name, uint Pid, float Volume, bool Muted, AudioSessionControl Session);

/// <summary>
/// The apps currently playing sound on the default speaker, so each can be turned up, down or muted
/// on its own (a volume mixer). Reads the Windows audio sessions through NAudio.
/// </summary>
internal sealed class AudioMixerService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;

    /// <summary>A fresh snapshot of the apps making sound, loudest-named first; empty on any trouble.</summary>
    public List<MixerApp> Snapshot()
    {
        try
        {
            // Follow the default output; dropping the old device now keeps the last snapshot's sessions
            // valid until this call (the panel acts on them between refreshes).
            _device?.Dispose();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = _device.AudioSessionManager;
            sessions.RefreshSessions();
            var apps = new List<MixerApp>();
            for (var i = 0; i < sessions.Sessions.Count; i++)
            {
                var s = sessions.Sessions[i];
                if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                var pid = s.GetProcessID;
                var name = s.IsSystemSoundsSession ? "System sounds" : NameFor(pid, s);
                if (string.IsNullOrWhiteSpace(name)) continue;
                apps.Add(new MixerApp(name, pid, s.SimpleAudioVolume.Volume, s.SimpleAudioVolume.Mute, s));
            }
            // One row per app: fold an app's several sessions into its loudest.
            return [.. apps.GroupBy(a => a.Name)
                .Select(g => g.OrderByDescending(a => a.Volume).First())
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex)
        {
            Log.Write($"Mixer: couldn't read app volumes: {ex.Message}");
            return [];
        }
    }

    public static void SetVolume(MixerApp app, float level)
    {
        try { app.Session.SimpleAudioVolume.Volume = Math.Clamp(level, 0, 1); if (level > 0) app.Session.SimpleAudioVolume.Mute = false; }
        catch (Exception ex) { Log.Write($"Mixer: set volume failed: {ex.Message}"); }
    }

    public static void ToggleMute(MixerApp app)
    {
        try { app.Session.SimpleAudioVolume.Mute = !app.Session.SimpleAudioVolume.Mute; }
        catch (Exception ex) { Log.Write($"Mixer: mute failed: {ex.Message}"); }
    }

    private static string NameFor(uint pid, AudioSessionControl s)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc!;
            if (!string.IsNullOrWhiteSpace(p.ProcessName)) return p.ProcessName;
        }
        catch { }
        return string.IsNullOrWhiteSpace(s.DisplayName) ? "" : s.DisplayName;
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
