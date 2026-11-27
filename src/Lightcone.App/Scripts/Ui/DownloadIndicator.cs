using Godot;
using Lightcone.App.Screens;

namespace Lightcone.App.Ui;

/// <summary>
/// Shows that downloads are running: "⤓ 2 · 45%" with a thin progress bar, in the header
/// (controller) or the sidebar (desktop). Hidden while nothing is queued; click opens Downloads.
/// </summary>
public partial class DownloadIndicator : VBoxContainer
{
    private readonly Label _label;
    private readonly ProgressBar _bar;
    private double _clock;

    public DownloadIndicator(int fontSize = 17)
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        TooltipText = "Downloads";
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        AddThemeConstantOverride("separation", 3);
        _label = UiTheme.Label("", fontSize, UiTheme.AccentText);
        _label.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_label);
        _bar = new ProgressBar { ShowPercentage = false, MinValue = 0, MaxValue = 1, Step = 0, CustomMinimumSize = new Vector2(0, 4), MouseFilter = MouseFilterEnum.Ignore };
        _bar.AddThemeStyleboxOverride("background", UiTheme.Box(UiTheme.Dim with { A = 0.25f }, 0, 2));
        _bar.AddThemeStyleboxOverride("fill", UiTheme.Box(UiTheme.Accent, 0, 2));
        AddChild(_bar);
        GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && Main.Instance.TopOrNull is not DownloadsPage)
            {
                Main.Instance.Push(new DownloadsPage());
            }
        };
    }

    public override void _Process(double delta)
    {
        _clock -= delta;
        if (_clock > 0 || Main.Instance.Host is not { } host)
        {
            return;
        }
        _clock = 0.5;
        var (pending, fraction) = host.Downloads.Activity;
        Visible = pending > 0;
        if (!Visible)
        {
            return;
        }
        var text = fraction is { } f ? $"⤓ {pending}  ·  {f * 100:0}%" : $"⤓ {pending}  ·  …";
        if (_label.Text != text)
        {
            _label.Text = text;
        }
        _bar.Value = fraction ?? 0;
    }
}
