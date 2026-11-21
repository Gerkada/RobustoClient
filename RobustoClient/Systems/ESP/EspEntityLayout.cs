using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace RobustoClient.Systems.ESP;

public sealed class EspLayoutManager
{
    private DrawingHandleScreen? _handle;
    private readonly Font _font;
    private Vector2 _baseScreenPos;
    
    private readonly List<LayoutElement> _top = new();
    private readonly List<LayoutElement> _bottom = new();
    private readonly List<LayoutElement> _left = new();
    private readonly List<LayoutElement> _right = new();
    private int _nextOrder;

    private static readonly Comparison<LayoutElement> PriorityDescending = (a, b) =>
    {
        int cmp = b.Priority.CompareTo(a.Priority);
        return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
    };

    private readonly struct LayoutElement
    {
        public readonly string Text;
        public readonly Color Color;
        public readonly int Priority;
        public readonly int Order;

        public LayoutElement(string text, Color color, int priority, int order)
        {
            Text = text;
            Color = color;
            Priority = priority;
            Order = order;
        }
    }

    public EspLayoutManager(Font font)
    {
        _font = font;
    }

    public EspLayoutManager(DrawingHandleScreen handle, Font font, Vector2 baseScreenPos)
    {
        _handle = handle;
        _font = font;
        _baseScreenPos = baseScreenPos;
    }

    public void Reset(DrawingHandleScreen handle, Vector2 baseScreenPos)
    {
        _handle = handle;
        _baseScreenPos = baseScreenPos;
        _nextOrder = 0;
        _top.Clear();
        _bottom.Clear();
        _left.Clear();
        _right.Clear();
    }

    public void Add(EspDock dock, string text, Color color, int priority)
    {
        var element = new LayoutElement(text, color, priority, _nextOrder++);
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
        if (_handle == null) return;

        // Most entities fill one dock and leave the other three empty, so the count guard skips the
        // sort setup on the empty lists rather than paying it four times per entity per frame.
        // Top: stacks upwards, highest priority closest to the entity.
        if (_top.Count > 1) _top.Sort(PriorityDescending);
        float currentY = _baseScreenPos.Y - 45f;
        foreach (var el in _top)
        {
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X - 40f, currentY), el.Text, el.Color);
            currentY -= 12f;
        }

        // Bottom: stacks downwards.
        if (_bottom.Count > 1) _bottom.Sort(PriorityDescending);
        currentY = _baseScreenPos.Y + 10f;
        foreach (var el in _bottom)
        {
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X - 40f, currentY), el.Text, el.Color);
            currentY += 12f;
        }

        // Right: stacks downwards from centre right.
        if (_right.Count > 1) _right.Sort(PriorityDescending);
        currentY = _baseScreenPos.Y - 30f;
        foreach (var el in _right)
        {
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X + 40f, currentY), el.Text, el.Color);
            currentY += 12f;
        }

        // Left: stacks downwards from centre left.
        if (_left.Count > 1) _left.Sort(PriorityDescending);
        currentY = _baseScreenPos.Y - 30f;
        foreach (var el in _left)
        {
            // Simple offset, does not measure string width.
            _handle.DrawString(_font, new Vector2(_baseScreenPos.X - 80f, currentY), el.Text, el.Color);
            currentY += 12f;
        }
    }
}
