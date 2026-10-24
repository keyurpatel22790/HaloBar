using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI;

/// <summary>
/// One page of the open island (Home, Music, Shelf…). Each island has its own panel objects, so
/// scroll positions and half-typed text stay per monitor; the data comes from the shared <see cref="Hub"/>.
/// </summary>
internal abstract class Panel(Hub hub)
{
    protected Hub Hub { get; } = hub;
    protected Theme Theme => Hub.Theme;

    public abstract string Title { get; }
    public abstract void Icon(SKCanvas c, float cx, float cy, float size, SKColor color);

    /// <summary>Shown in the tab bar. Panels whose feature is off hide themselves.</summary>
    public virtual bool Available => true;
    /// <summary>A count shown on the tab (new notifications…).</summary>
    public virtual int Badge => 0;

    /// <summary>Small text buttons at the right of the tab bar, before the clock.</summary>
    public virtual IEnumerable<(string Label, Action Click)> Actions => [];

    /// <summary>How soon this panel wants to be redrawn while shown, in seconds.</summary>
    public virtual double FrameInterval => 1;

    public virtual void Opened() { }

    /// <summary>Advances the panel's own animations (scrolling) by <paramref name="dt"/> seconds.</summary>
    public virtual void Update(double dt) { }

    /// <summary>Drawn under the tab bar, over the whole island (the album streaks).</summary>
    public virtual void Background(Ui ui, SKRect island) { }

    public abstract void Draw(Ui ui, SKRect body);

    /// <summary>Scroll wheel over the open island; return false to let it change the volume.</summary>
    public virtual bool Wheel(float notches) => false;
}
