using System.Windows;
using System.Windows.Media;

namespace ChatLan.Services;

/// <summary>
/// Recreates the WhatsApp chat wallpaper (a tiled sheet of small doodles) procedurally.
/// Shapes are laid out deterministically so the texture is stable between launches.
/// </summary>
public static class WallpaperFactory
{
    private static string[] Shapes()
    {
        return new[]
        {
            // heart
            "M-14,2 C-14,-6 -6,-10 0,-4 C6,-10 14,-6 14,2 C14,10 0,16 0,16 C0,16 -14,10 -14,2 Z",
            // star
            "M0,-16 L5,-5 L17,-4 L8,4 L11,16 L0,9 L-11,16 L-8,4 L-17,-4 L-5,-5 Z",
            // speech bubble
            "M-16,-11 Q-16,-17 -10,-17 L10,-17 Q16,-17 16,-11 L16,3 Q16,9 10,9 L-6,9 L-14,15 L-13,8 Q-16,7 -16,3 Z",
            // music note
            "M-4,12 C-9,12 -12,9 -12,6 C-12,3 -9,1 -5,1 C-2,1 1,2 2,4 L2,-16 L16,-19 L16,7 C16,11 13,14 9,14 C5,14 2,12 2,9",
            // coffee cup
            "M-14,-8 L8,-8 L8,4 Q8,13 -2,13 Q-13,13 -13,4 Z M8,-3 L14,-3 Q19,-3 19,2 Q19,7 13,7 M-17,15 L13,15",
            // flower
            "M0,0 C-4,-8 -2,-14 0,-14 C2,-14 4,-8 0,0 C8,-4 14,-2 14,0 C14,2 8,4 0,0 C8,4 10,10 7,12 C5,14 2,8 0,0 C-2,8 -5,14 -7,12 C-10,10 -8,4 0,0 C-8,4 -14,2 -14,0 C-14,-2 -8,-4 0,0 Z",
            // leaf
            "M-15,12 C-15,-2 0,-13 16,-13 C16,3 5,14 -9,14 M-15,12 C-8,4 0,-2 10,-8",
            // camera
            "M-17,-6 L-8,-6 L-5,-11 L7,-11 L10,-6 L17,-6 L17,11 L-17,11 Z M0,9 A8,8 0 1 1 0,-5 A8,8 0 1 1 0,9 Z",
            // pin
            "M0,16 C0,16 -11,4 -11,-4 A11,11 0 1 1 11,-4 C11,4 0,16 0,16 Z M0,-1 A4,4 0 1 1 0,-9 A4,4 0 1 1 0,-1 Z",
            // gift
            "M-14,-6 L14,-6 L14,14 L-14,14 Z M-16,-11 L16,-11 L16,-6 L-16,-6 Z M0,-11 L0,14 M-6,-11 C-13,-11 -13,-19 -6,-19 C-1,-19 0,-13 0,-11 C0,-13 1,-19 6,-19 C13,-19 13,-11 6,-11",
            // cloud
            "M-16,6 Q-20,-2 -13,-5 Q-11,-14 -2,-12 Q3,-19 10,-14 Q18,-13 16,-4 Q22,0 18,6 Z",
            // arrow
            "M-15,10 L10,-14 M-2,-14 L10,-14 L10,-2",
            // planet
            "M0,0 A10,10 0 1 1 0.1,0 Z M-18,4 Q0,-18 18,-6",
            // scissors
            "M-14,-12 L8,8 M-14,12 L8,-8 M6,6 A4,4 0 1 1 13,11 A4,4 0 1 1 6,6 Z M6,-6 A4,4 0 1 1 13,-11 A4,4 0 1 1 6,-6 Z",
            // sun
            "M0,-10 A10,10 0 1 1 0,10 A10,10 0 1 1 0,-10 Z M0,-18 L0,-14 M0,14 L0,18 M-18,0 L-14,0 M14,0 L18,0 M-13,-13 L-10,-10 M10,10 L13,13 M-13,13 L-10,10 M10,-10 L13,-13",
            // moon
            "M4,-16 A16,16 0 1 0 16,6 A12,12 0 1 1 4,-16 Z",
            // headphones
            "M-15,4 A15,15 0 1 1 15,4 L15,12 M-15,4 L-15,12 M-19,8 L-11,8 L-11,19 L-19,19 Z M11,8 L19,8 L19,19 L11,19 Z",
            // clock
            "M0,-15 A15,15 0 1 1 0,15 A15,15 0 1 1 0,-15 Z M0,-8 L0,0 L7,4",
            // balloon
            "M0,-14 A11,12 0 1 1 0,10 A11,12 0 1 1 0,-14 Z M0,10 L0,14 M0,14 Q-1,18 4,18",
            // envelope
            "M-16,-10 L16,-10 L16,10 L-16,10 Z M-16,-10 L0,3 L16,-10",
            // cookie
            "M0,-15 A15,15 0 1 1 0,15 A15,15 0 1 1 0,-15 Z M-6,-6 A2,2 0 1 1 0,-6 M6,-2 A2,2 0 1 1 0,-2 M-2,7 A2,2 0 1 1 0,7",
            // pizza
            "M0,-16 L17,14 L-17,14 Z M-6,-2 A2,2 0 1 1 0,-2 M6,4 A2,2 0 1 1 0,4",
            // sparkle
            "M0,-14 L3,-3 L14,0 L3,3 L0,14 L-3,3 L-14,0 L-3,-3 Z",
            // shopping bag
            "M-13,-6 L13,-6 L15,15 L-15,15 Z M-6,-6 L-6,-9 A6,6 0 1 1 6,-9 L6,-6",
            // paper plane
            "M-16,0 L17,-12 L8,15 L1,5 Z M1,5 L17,-12",
            // wifi
            "M-16,-4 Q0,-17 16,-4 M-11,2 Q0,-8 11,2 M-5,8 Q0,3 5,8 M0,12 A1.6,1.6 0 1 1 0,12.2",
            // paw
            "M0,2 A9,8 0 1 1 0,18 A9,8 0 1 1 0,2 Z M-12,-6 A3.5,4 0 1 1 0,-6 M-5,-12 A3.5,4 0 1 1 0,-12 M5,-12 A3.5,4 0 1 1 0,-12 M12,-6 A3.5,4 0 1 1 0,-6",
            // lightbulb
            "M0,-17 A12,12 0 0 1 7,5 L4,9 L-4,9 L-7,5 A12,12 0 0 1 0,-17 Z M-4,13 L4,13 M-3,17 L3,17"
        };
    }

    public static DrawingBrush Create(bool dark)
    {
        var tile = BuildDrawing(dark);
        return new DrawingBrush(tile)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 460, 460),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Viewbox = new Rect(0, 0, 460, 460),
            ViewboxUnits = BrushMappingMode.Absolute
        };
    }

    public static Brush CreateTileBrush(bool dark)
    {
        var tile = BuildDrawing(dark);
        return new DrawingBrush(tile)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 460, 460),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Viewbox = new Rect(0, 0, 460, 460),
            ViewboxUnits = BrushMappingMode.Absolute
        };
    }

    private static DrawingGroup BuildDrawing(bool dark)
    {
        var ink = dark
            ? Color.FromArgb(20, 255, 255, 255)
            : Color.FromArgb(18, 0, 0, 0);
        var pen = new Pen(new SolidColorBrush(ink), 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var group = new DrawingGroup();

        var shapes = Shapes();
        var rnd = new Random(20261118);
        for (var i = 0; i < 26; i++)
        {
            var shapeIndex = (i * 7 + i / 3) % shapes.Length;
            var geo = Geometry.Parse(shapes[shapeIndex]);
            var transform = new TransformGroup();
            transform.Children.Add(new RotateTransform(rnd.Next(-28, 28)));
            transform.Children.Add(new ScaleTransform(0.75 + rnd.NextDouble() * 0.55, 0.75 + rnd.NextDouble() * 0.55));
            transform.Children.Add(new TranslateTransform(40 + rnd.Next(0, 380), 40 + rnd.Next(0, 380)));
            transform.Freeze();
            geo.Freeze();
            var shape = new DrawingGroup { Transform = transform };
            shape.Children.Add(new GeometryDrawing(null, pen, geo));
            shape.Freeze();
            group.Children.Add(shape);
        }
        group.Freeze();
        return group;
    }
}
