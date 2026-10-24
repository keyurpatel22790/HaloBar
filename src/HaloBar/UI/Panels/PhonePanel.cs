using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>A QR code that opens a page on your phone: shelf files to download, a box to send files back.</summary>
internal sealed class PhonePanel(Hub hub, PhoneShareService phone) : Panel(hub)
{
    public override string Title => "Phone";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Phone(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.PhoneShare;
    public override double FrameInterval => 1;

    public override void Draw(Ui ui, SKRect body)
    {
        phone.Touch();
        var box = new SKRect(body.Left + 22, body.Top + 2, body.Left + 22 + 138, body.Top + 140);
        ui.Fill(box, 14, SKColors.White);
        if (phone.Code is { } code)
        {
            // Black modules on white, with the standard quiet zone inside the white box.
            var n = code.GetLength(0);
            var cell = (box.Width - 16) / n;
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    if (code[x, y]) ui.Canvas.DrawRect(box.Left + 8 + x * cell, box.Top + 8 + y * cell, cell + 0.4f, cell + 0.4f, paint);
        }
        else ui.Text(phone.Status.Length > 0 ? phone.Status : "Starting…", box.MidX, box.MidY, box.Width - 12, 11, Theme.Weight.Regular, new SKColor(0x55, 0x55, 0x55), SKTextAlign.Center);

        var x0 = box.Right + 22;
        var width = body.Right - 22 - x0;
        ui.Text("Scan with your phone's camera", x0, body.Top + 20, width, 14, Theme.Weight.SemiBold);
        ui.Text("Same Wi-Fi as this PC. Download what's on the shelf, or send photos and files here.", x0, body.Top + 40, width, 11.5f, Theme.Weight.Regular, Theme.Dim);
        if (phone.Url != null) ui.Text(phone.Url, x0, body.Top + 62, width, 11, Theme.Weight.Regular, Theme.Accent);
        var shelf = Hub.Shelf.Files.Count;
        ui.Text(shelf == 0 ? "Nothing on the shelf yet: drop files on the island to share them." : $"{shelf} file{(shelf == 1 ? "" : "s")} on the shelf to download.",
            x0, body.Top + 86, width, 11.5f, Theme.Weight.Regular, Theme.Text);
        ui.Text($"Uploads go to {phone.UploadFolder}", x0, body.Top + 104, width, 10.5f, Theme.Weight.Regular, Theme.Faint);
        if (phone.NetworkIsPublic)
        {
            ui.Circle(x0 + 4, body.Top + 124, 3.5f, new SKColor(0xFF, 0x9F, 0x0A));
            ui.Text("This network is Public: in Windows settings set it to Private so phones can connect.", x0 + 14, body.Top + 128, width - 14, 10.5f,
                Theme.Weight.Regular, new SKColor(0xFF, 0x9F, 0x0A));
        }
    }
}
