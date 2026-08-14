using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace RobustoClient.Systems.ESP;

public sealed class EspLayoutManager
{
    private readonly DrawingHandleScreen _handle;
    private readonly Font _font;
    private readonly Vector2 _baseScreenPos;
    
    private readonly List<LayoutElement> _top = new();
    private readonly List<LayoutElement> _bottom = new();
    private readonly List<LayoutElement> _left = new();
    private readonly List<LayoutElement> _right = new();

    private readonly struct LayoutElement
    {
        public readonly string Text;
        public readonly Color Color;
        public readonly int Priority;

        public LayoutElement(string text, Color color, int priority)
        {
            Text = text;
            Color = color;
            Priority = priority;
        }
    }

    public EspLayoutManager(DrawingHandleScreen handle, Font font, Vector2 baseScreenPos)
    {
        _handle = handle;
        _font = font;
        _baseScreenPos = baseScreenPos;
    }

    public void Add(EspDock dock, string text, Color color, int priority)
    {
        var element = new LayoutElement(text, color, priority);
        switch (dock)
        {
            case EspDock.Top: _top.Add(element); break;
            case EspDock.Bottom: _bottom.Add(element); break;
            case EspDock.Left: _left.Add(element); break;
            case EspDock.Right: _right.Add(element); break;
        }
    }

    public void DrawAll()
    {
        // Top: draw stacking upwards. Highest priority is closest to entity (lowest Y offset)
        var topSorted = _top.OrderByDescending(e => e.Priority).ToList();
        float currentY = _baseScreenPos.Y - 45f;
        foreach (var el in topSorted)
        {
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X - 40f, currentY), el.Text, el.Color);
            currentY -= 12f;
        }

        // Bottom: draw stacking downwards.
        var bottomSorted = _bottom.OrderByDescending(e => e.Priority).ToList();
        currentY = _baseScreenPos.Y + 10f;
        foreach (var el in bottomSorted)
        {
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X - 40f, currentY), el.Text, el.Color);
            currentY += 12f;
        }

        // Right: stack downwards from center right
        var rightSorted = _right.OrderByDescending(e => e.Priority).ToList();
        currentY = _baseScreenPos.Y - 30f;
        foreach (var el in rightSorted)
        {
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X + 40f, currentY), el.Text, el.Color);
            currentY += 12f;
        }

        // Left: stack downwards from center left
        var leftSorted = _left.OrderByDescending(e => e.Priority).ToList();
        currentY = _baseScreenPos.Y - 30f;
        foreach (var el in leftSorted)
        {
            // Simple offset, ideally would measure string width
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X - 80f, currentY), el.Text, el.Color);
            currentY += 12f;
        }
    }
}
