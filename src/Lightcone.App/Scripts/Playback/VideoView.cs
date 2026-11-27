using Godot;
using Lightcone.App.Ui;

namespace Lightcone.App.Playback;

/// <summary>
/// Shows the player's picture, letterboxed. mpv leaves alpha at 0 for opaque video, so a
/// one-line shader forces it to 1 (otherwise the background would show through).
/// </summary>
public partial class VideoView : Control
{
    private static Shader? _opaque;
    private readonly TextureRect _rect;

    public VideoView()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        var bg = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        _opaque ??= new Shader { Code = "shader_type canvas_item; void fragment() { COLOR = vec4(texture(TEXTURE, UV).rgb, 1.0); }" };
        _rect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Material = new ShaderMaterial { Shader = _opaque },
            MouseFilter = MouseFilterEnum.Ignore,
            TextureFilter = TextureFilterEnum.Linear,
        };
        _rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_rect);
    }

    public override void _Process(double delta)
    {
        var svc = PlayerService.Instance;
        var tex = svc.HasVideo ? svc.Texture : null;
        if (_rect.Texture != tex)
        {
            _rect.Texture = tex;
        }
        // Paced (idle playback): draw only when a frame is due, so the screen follows the video's
        // rate. Otherwise every frame, so menus and controls stay at the display's rate.
        if (!svc.Paced || svc.FramePending)
        {
            _rect.QueueRedraw();
        }
    }

    /// <summary>Frees the shared shader while .NET still runs (Main._ExitTree).</summary>
    public static void ReleaseShared() => _opaque = null;
}
