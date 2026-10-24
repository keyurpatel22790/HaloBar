using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;

namespace HaloBar.Services;

/// <summary>
/// Quick settings for the Controls panel: Wi-Fi and Bluetooth radios, Night light, and the
/// default speaker and microphone.
/// </summary>
internal sealed class ControlsService
{
    private Radio? _wifi, _bluetooth;

    public bool? WifiOn => _wifi == null ? null : _wifi.State == RadioState.On;
    public bool? BluetoothOn => _bluetooth == null ? null : _bluetooth.State == RadioState.On;
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                Log.Write("Controls: Windows didn't allow radio control");
                return;
            }
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (radio.Kind == RadioKind.WiFi) _wifi ??= radio;
                else if (radio.Kind == RadioKind.Bluetooth) _bluetooth ??= radio;
                else continue;
                radio.StateChanged += (_, _) => Changed?.Invoke();
            }
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"Controls: radios unavailable: {ex.Message}");
        }
    }

    public void ToggleWifi() => Toggle(_wifi);
    public void ToggleBluetooth() => Toggle(_bluetooth);

    private void Toggle(Radio? radio)
    {
        if (radio == null) return;
        var next = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await radio.SetStateAsync(next);
                if (result != RadioAccessStatus.Allowed) Log.Write($"Controls: {radio.Kind} switch refused ({result})");
            }
            catch (Exception ex)
            {
                Log.Write($"Controls: {radio.Kind} switch failed: {ex.Message}");
            }
            Changed?.Invoke();
        });
    }

    // ───────────────────────────── Night light ─────────────────────────────

    // Windows keeps Night light's on/off state in a CloudStore blob. Byte 18 is 0x15 when on and
    // 0x13 when off; "on" also carries two extra bytes (0x10 0x00) at offset 23. Windows watches
    // the value, so writing it switches Night light like the Action Center button does.
    private const string NightLightKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    public bool? NightLightOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(NightLightKey);
            return key?.GetValue("Data") is byte[] { Length: > 18 } data ? data[18] == 0x15 : null;
        }
    }

    public void ToggleNightLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NightLightKey, writable: true);
            if (key?.GetValue("Data") is not byte[] { Length: > 24 } data) return;
            var on = data[18] == 0x15;
            var list = data.ToList();
            if (on)
            {
                list[18] = 0x13;
                if (list[23] == 0x10 && list[24] == 0x00) list.RemoveRange(23, 2);
            }
            else
            {
                list[18] = 0x15;
                list.InsertRange(23, [0x10, 0x00]);
            }
            // Bump the change counter so Windows notices the write.
            for (var i = 10; i < 15; i++)
            {
                if (list[i] != 0xFF) { list[i]++; break; }
                list[i] = 0;
            }
            key.SetValue("Data", list.ToArray(), RegistryValueKind.Binary);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentOutOfRangeException)
        {
            Log.Write($"Controls: Night light switch failed: {ex.Message}");
        }
    }

    // ───────────────────────────── audio devices ─────────────────────────────

    public static (string Name, string Id)[] Devices(DataFlow flow)
    {
        using var e = new MMDeviceEnumerator();
        return [.. e.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d =>
        {
            using (d) return (d.FriendlyName, d.ID);
        })];
    }

    public static string? DefaultName(DataFlow flow)
    {
        using var e = new MMDeviceEnumerator();
        if (!e.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var d)) return null;
        using (d) return d.FriendlyName;
    }

    /// <summary>Makes the next active device the default for every role. Returns its name.</summary>
    public static string? CycleDefault(DataFlow flow)
    {
        var devices = Devices(flow);
        if (devices.Length < 2) return devices.FirstOrDefault().Name;
        using var e = new MMDeviceEnumerator();
        var current = e.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var d) ? d.ID : "";
        d?.Dispose();
        var next = devices[(Array.FindIndex(devices, x => x.Id == current) + 1) % devices.Length];
        SetDefault(next.Id);
        return next.Name;
    }

    /// <summary>
    /// Sets the default endpoint through IPolicyConfig, the interface the Sound control panel uses.
    /// It is undocumented but has been stable since Windows 7.
    /// </summary>
    private static void SetDefault(string id)
    {
        try
        {
            var config = (IPolicyConfig)new PolicyConfigClient();
            for (var role = 0; role < 3; role++) config.SetDefaultEndpoint(id, role);
            Marshal.ReleaseComObject(config);
        }
        catch (Exception ex)
        {
            Log.Write($"Controls: switching audio device failed: {ex.Message}");
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        void SetEndpointVisibility();
    }
}
