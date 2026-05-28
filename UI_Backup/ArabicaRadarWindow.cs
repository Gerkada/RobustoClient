using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;
using System.Numerics;

namespace ArabicaCliento.UI;

public sealed class ArabicaRadarWindow : DefaultWindow
{
    public ArabicaRadarWindow()
    {
        Title = "Arabica Tactical Radar";
        Resizable = false;
        MinSize = new Vector2(280, 280);
        SetSize = new Vector2(280, 280);

        var panel = new PanelContainer { Margin = new Thickness(8) };
        panel.AddChild(new ArabicaRadarControl());
        Contents.AddChild(panel);
    }
}