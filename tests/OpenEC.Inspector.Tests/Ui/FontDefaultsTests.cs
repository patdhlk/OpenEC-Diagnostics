using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace OpenEC.Inspector.Tests.Ui;

// Guards the Windows-11 "all text invisible" regression: the app must default to the bundled Inter
// font rather than the OS font. When the default fell through to "Segoe UI" (mapped to the variable
// "Segoe UI Variable Text"), Avalonia 12 failed to shape it and every label rendered blank.
public class FontDefaultsTests
{
    [AvaloniaFact]
    public void The_default_font_family_is_the_bundled_inter()
    {
        Assert.Equal("Inter", FontManager.Current.DefaultFontFamily.Name);
    }

    [AvaloniaFact]
    public void The_default_font_family_reference_resolves_to_a_real_glyph_typeface()
    {
        // Proves fonts:Inter#Inter is a live, loadable reference (not a typo that silently falls
        // back), so text actually has glyphs to draw.
        var resolved = FontManager.Current.TryGetGlyphTypeface(
            new Typeface(FontManager.Current.DefaultFontFamily), out _);

        Assert.True(resolved);
    }
}
