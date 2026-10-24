using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>A camera mirror with microphone and camera switches, and who is using them.</summary>
internal sealed class CallPanel(Hub hub, CameraService camera) : Panel(hub)
{
    public override string Title => "Call";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Camera(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.CameraEnabled;
    public override double FrameInterval => camera.On ? 1 / 15.0 : 1;

    public override void Draw(Ui ui, SKRect body)
    {
        var mirror = new SKRect(body.Left + 18, body.Top + 2, body.Left + 18 + 186, body.Top + 140);
        ui.Fill(mirror, 16, Theme.Overlay(14));
        if (camera.Frame is { } frame)
        {
            // Mirrored, like a real mirror.
            ui.Canvas.Save();
            ui.Canvas.Scale(-1, 1, mirror.MidX, 0);
            ui.Image(frame, mirror, 16);
            ui.Canvas.Restore();
        }
        else
        {
            Icons.Camera(ui.Canvas, mirror.MidX, mirror.MidY - 10, 28, Theme.Faint);
            ui.Text(camera.Status.Length > 0 ? camera.Status : "Camera off", mirror.MidX, mirror.MidY + 22, mirror.Width - 16, 11.5f,
                Theme.Weight.Regular, Theme.Dim, SKTextAlign.Center);
        }

        var x0 = mirror.Right + 22;
        var width = body.Right - 22 - x0;
        var callApp = Hub.Privacy.MicrophoneApps.FirstOrDefault();
        ui.Text(callApp != null ? $"In a call · {callApp}" : "Check your look", x0, body.Top + 20, width, 15, Theme.Weight.SemiBold);
        ui.Text(callApp != null ? "Your microphone is open in this app." : "Turn the mirror on before you join.", x0, body.Top + 40, width, 11.5f, Theme.Weight.Regular, Theme.Dim);

        var y = body.Top + 74;
        var x = ui.Chip(x0, y, camera.On ? "Turn mirror off" : "Turn mirror on", () => _ = camera.ToggleAsync(), camera.On, 12, accent: !camera.On).Right + 8;
        var muted = Hub.Microphone.Muted;
        ui.Chip(x, y, muted ? "Unmute microphone" : "Mute microphone", Hub.Microphone.ToggleMute, muted, 12);

        var p = Hub.Privacy;
        var line = (p.CameraInUse ? "Camera: " + string.Join(", ", p.CameraApps) : "Camera: not in use")
                   + "   ·   " + (p.MicrophoneInUse ? "Microphone: " + string.Join(", ", p.MicrophoneApps) : "Microphone: not in use");
        ui.Text(line, x0, body.Top + 112, width, 11, Theme.Weight.Regular, Theme.Faint);
        ui.Text("The mirror stays on this PC; nothing is recorded.", x0, body.Top + 130, width, 10.5f, Theme.Weight.Regular, Theme.Faint);
    }
}
