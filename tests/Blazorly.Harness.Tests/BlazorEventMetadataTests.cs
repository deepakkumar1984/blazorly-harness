using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>The file tree builds its context-menu wiring with manual RenderTreeBuilder
/// frames. The Razor compiler emits stopPropagation metadata as a plain suffixed
/// attribute frame, so this pins the handwritten frames to exactly that contract —
/// a bubbled node right-click must not reset the menu to the root one (which would
/// hide Rename/Delete).</summary>
public class BlazorEventMetadataTests
{
    [Fact]
    public void ManualStopPropagationFrame_UsesCompilerAttributeContract()
    {
        var builder = new RenderTreeBuilder();
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "oncontextmenu", EventCallback.Factory.Create<MouseEventArgs>(this, static _ => { }));
        builder.AddAttribute(2, "oncontextmenu:preventDefault", true);
        builder.AddAttribute(3, "oncontextmenu:stopPropagation", true);
        builder.CloseElement();

        var frames = builder.GetFrames().Array;
        var stop = frames[3];
        Assert.Equal(RenderTreeFrameType.Attribute, stop.FrameType);
        Assert.Equal("oncontextmenu:stopPropagation", stop.AttributeName);
        Assert.Equal(true, stop.AttributeValue);
    }
}
