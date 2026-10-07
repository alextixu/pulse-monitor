using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Pulse.App.Controls;

/// <summary>
/// Small rounded label: optional icon + text, coloured by <see cref="Tone"/> (tinted background, solid foreground).
/// Template lives in Controls/StatPill.axaml. Usage:
/// <c>&lt;controls:StatPill Text="61°C" Icon="{StaticResource IconThermometer}" Tone="Warning" /&gt;</c>
/// </summary>
public class StatPill : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatPill, string?>(nameof(Text));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<StatPill, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<BccTone> ToneProperty =
        AvaloniaProperty.Register<StatPill, BccTone>(nameof(Tone), BccTone.Neutral);

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<StatPill, double>(nameof(IconSize), 12d);

    /// <summary>True → solid tone background with white text instead of the tinted look.</summary>
    public static readonly StyledProperty<bool> IsSolidProperty =
        AvaloniaProperty.Register<StatPill, bool>(nameof(IsSolid));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public BccTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public bool IsSolid { get => GetValue(IsSolidProperty); set => SetValue(IsSolidProperty, value); }

    public StatPill()
    {
        UpdatePseudoClasses();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty || change.Property == IsSolidProperty)
        {
            UpdatePseudoClasses();
        }
    }

    private void UpdatePseudoClasses()
    {
        var tone = Tone;
        PseudoClasses.Set(":neutral", tone == BccTone.Neutral);
        PseudoClasses.Set(":accent", tone == BccTone.Accent);
        PseudoClasses.Set(":success", tone == BccTone.Success);
        PseudoClasses.Set(":warning", tone == BccTone.Warning);
        PseudoClasses.Set(":danger", tone == BccTone.Danger);
        PseudoClasses.Set(":purple", tone == BccTone.Purple);
        PseudoClasses.Set(":teal", tone == BccTone.Teal);
        PseudoClasses.Set(":pink", tone == BccTone.Pink);
        PseudoClasses.Set(":solid", IsSolid);
    }
}
