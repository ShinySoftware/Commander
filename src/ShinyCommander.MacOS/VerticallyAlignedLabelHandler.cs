using CoreGraphics;
using Microsoft.Maui.Platforms.MacOS.Handlers;

namespace ShinyCommander;

/// <summary>
/// maui-labs' AppKit <see cref="LabelHandler"/> never maps <c>VerticalTextAlignment</c>: NSTextField draws its
/// text at the top of its frame, so every label taller than its text (a ribbon tab, a chip, a row cell, a
/// button caption) sits high. This handler shifts the text down with the cell's top inset at arrange time.
/// The extra inset is removed again before measuring, so it never feeds back into the label's desired size.
/// </summary>
/// <remarks>Remove once maui-labs maps VerticalTextAlignment itself.</remarks>
public class VerticallyAlignedLabelHandler : LabelHandler
{
    /// <summary>Re-aligns when VerticalTextAlignment changes after the label was laid out (call once at startup).</summary>
    public static void Register()
        => Mapper.AppendToMapping(nameof(ITextAlignment.VerticalTextAlignment), (h, _) =>
        {
            if (h is VerticallyAlignedLabelHandler vh && vh.lastFrame is { } frame)
                vh.Align(frame);
        });

    Rect? lastFrame;

    AppKit.NSEdgeInsets PaddingInsets()
    {
        var p = this.VirtualView.Padding;
        return new AppKit.NSEdgeInsets((nfloat)p.Top, (nfloat)p.Left, (nfloat)p.Bottom, (nfloat)p.Right);
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if (this.PlatformView is { } view)
            view.TextInsets = this.PaddingInsets();
        var size = base.GetDesiredSize(widthConstraint, heightConstraint);
        if (this.lastFrame is { } frame)
            this.Align(frame);
        return size;
    }

    public override void PlatformArrange(Rect rect)
    {
        base.PlatformArrange(rect);
        this.lastFrame = rect;
        this.Align(rect);
    }

    void Align(Rect frame)
    {
        if (this.PlatformView is not { } view || this.VirtualView is not ITextAlignment align)
            return;

        var insets = this.PaddingInsets();
        if (align.VerticalTextAlignment != TextAlignment.Start && frame.Height > 0 && frame.Width > 0)
        {
            var width = frame.Width - (double)insets.Left - (double)insets.Right;
            var free = frame.Height - (double)insets.Top - (double)insets.Bottom;
            if (width > 0 && free > 0)
            {
                var text = view.Cell.CellSizeForBounds(new CGRect(0, 0, (nfloat)width, (nfloat)free)).Height;
                var slack = free - Math.Ceiling((double)text);
                if (slack > 0.5)
                {
                    var shift = align.VerticalTextAlignment == TextAlignment.End ? slack : Math.Floor(slack / 2);
                    insets = new AppKit.NSEdgeInsets(insets.Top + (nfloat)shift, insets.Left, insets.Bottom, insets.Right);
                }
            }
        }

        var current = view.TextInsets;
        if (current.Top != insets.Top || current.Left != insets.Left || current.Bottom != insets.Bottom || current.Right != insets.Right)
        {
            view.TextInsets = insets;
            view.NeedsDisplay = true;
        }
    }
}
