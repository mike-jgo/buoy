namespace Buoy.Core.Documents;

/// <summary>
/// The characters that start a list line, as the Mac app stores them.
///
/// Bullets are stored and shown as the marker plus one space. To-dos are
/// stored as the bare marker (<c>☐milk</c>): the Mac's <c>rtfContent()</c>
/// replaces the checkbox and its spacer with the marker alone. Builds before
/// <c>ce627a4</c> left the space in (<c>☐ milk</c>), so readers accept any
/// number of spaces after a to-do marker.
/// </summary>
public static class ListMarkers
{
    public const char Bullet = (char)0x2022;        // •  level 0
    public const char NestedBullet = (char)0x25E6;  // ◦  levels 1 and 2
    public const char Unchecked = (char)0x2610;     // ☐
    public const char Checked = (char)0x2611;       // ☑

    /// <summary>Points per nesting level (the Mac's <c>ListIndent.width</c>).</summary>
    public const double IndentWidth = 20;

    public static char BulletFor(int level) => level == 0 ? Bullet : NestedBullet;

    public static char TodoFor(bool isChecked) => isChecked ? Checked : Unchecked;

    /// <summary>The marker as written to <c>contentRTF</c>, or empty.</summary>
    public static string StoredPrefix(BuoyBlock block) => block.Type switch
    {
        BlockType.Bullet => $"{BulletFor(block.Level)} ",
        BlockType.Todo => TodoFor(block.Checked).ToString(),
        _ => "",
    };

    /// <summary>The marker as shown in the editor (always followed by one space), or empty.</summary>
    public static string EditorPrefix(BuoyBlock block) => block.Type switch
    {
        BlockType.Bullet => $"{BulletFor(block.Level)} ",
        BlockType.Todo => $"{TodoFor(block.Checked)} ",
        _ => "",
    };

    /// <summary>
    /// Reads a paragraph's leading marker. Returns the block type, whether a
    /// to-do is checked, and how many characters the marker and its spaces
    /// take (0 for a plain paragraph).
    /// </summary>
    public static (BlockType Type, bool IsChecked, int Length) Detect(string paragraphText)
    {
        if (paragraphText.Length == 0) return (BlockType.Paragraph, false, 0);

        switch (paragraphText[0])
        {
            case Bullet or NestedBullet when paragraphText.Length >= 2 && paragraphText[1] == ' ':
                return (BlockType.Bullet, false, 2);
            case Unchecked or Checked:
                var length = 1;
                while (length < paragraphText.Length && paragraphText[length] == ' ') length++;
                return (BlockType.Todo, paragraphText[0] == Checked, length);
            default:
                return (BlockType.Paragraph, false, 0);
        }
    }

    /// <summary>
    /// Nesting level from a paragraph's first-line position in points
    /// (RTF <c>\li + \fi</c>, or TOM's <c>LeftIndent + FirstLineIndent</c>).
    /// </summary>
    public static int LevelFromFirstLineIndent(double points) =>
        Math.Clamp((int)Math.Round(points / IndentWidth), 0, BuoyBlock.MaxLevel);
}
