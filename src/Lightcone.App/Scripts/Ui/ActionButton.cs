using Godot;

namespace Lightcone.App.Ui;

/// <summary>Plain full-width action button with an optional help line.</summary>
public partial class ActionButton : Button
{
    /// <summary>
    /// Tooltips are for the desktop. In the controller layout the hidden mouse can still rest on a
    /// card (after a touch), which would pop its title up over the art.
    /// </summary>
    public override string _GetTooltip(Vector2 atPosition) => UiLayout.Desktop ? TooltipText : "";

    public ActionButton(string text, Action onPress, string help = "")
    {
        Text = text;
        TooltipText = help;
        Alignment = HorizontalAlignment.Left;
        CustomMinimumSize = new Vector2(0, 54);
        Pressed += onPress;
        Anim.PressFlash(this);
    }
}
