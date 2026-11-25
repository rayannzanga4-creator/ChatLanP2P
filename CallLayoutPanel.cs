using System.Windows;
using System.Windows.Controls;
using ChatLan.ViewModels;

namespace ChatLan.Controls;

// Dispose les vignettes d'un appel pour qu'elles REMPLISSENT tout l'écran :
//  - appel à deux : l'autre personne en plein écran (Full) + ma vignette en coin (Pip)
//  - appel de groupe : grille qui occupe tout l'espace (Tile)
public class CallLayoutPanel : Panel
{
    // Mode de chaque vignette, posé par le style de conteneur (voir CallView.xaml)
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(CallTileMode), typeof(CallLayoutPanel),
        new FrameworkPropertyMetadata(CallTileMode.Tile,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static CallTileMode GetMode(DependencyObject o) => (CallTileMode)o.GetValue(ModeProperty);
    public static void SetMode(DependencyObject o, CallTileMode v) => o.SetValue(ModeProperty, v);

    // Zones réservées pour l'en-tête (nom, durée) et la barre de boutons, superposés à la vidéo
    const double TopBar = 92, BottomBar = 120, Gap = 6, PipMargin = 16;

    Rect[] Compute(Size size)
    {
        var kids = InternalChildren;
        var n = kids.Count;
        var rects = new Rect[n];
        if (n == 0 || size.Width <= 0 || size.Height <= 0) return rects;

        var full = -1;
        for (var i = 0; i < n; i++)
            if (GetMode(kids[i]) == CallTileMode.Full) { full = i; break; }

        if (full >= 0)
        {
            // l'autre personne : tout l'écran
            rects[full] = new Rect(0, 0, size.Width, size.Height);

            // ma vignette : petite, en bas à droite, au-dessus des boutons
            var pw = Math.Clamp(size.Width * 0.22, 130, 300);
            var ph = pw * 0.75;
            var y = Math.Max(TopBar, size.Height - ph - BottomBar);
            for (var i = 0; i < n; i++)
            {
                if (i == full) continue;
                rects[i] = new Rect(Math.Max(0, size.Width - pw - PipMargin), y, pw, ph);
                y = Math.Max(TopBar, y - ph - 10);
            }
            return rects;
        }

        // grille : on choisit le nombre de colonnes qui donne les plus grandes vignettes (format ~4:3)
        var areaTop = TopBar;
        var areaH = Math.Max(1, size.Height - TopBar - BottomBar);
        var cols = 1;
        var best = -1.0;
        for (var c = 1; c <= n; c++)
        {
            var r = (int)Math.Ceiling(n / (double)c);
            var cw = (size.Width - Gap * (c + 1)) / c;
            var ch = (areaH - Gap * (r + 1)) / r;
            if (cw <= 0 || ch <= 0) continue;
            var tw = Math.Min(cw, ch * 4.0 / 3.0);
            if (tw > best) { best = tw; cols = c; }
        }
        var rows = (int)Math.Ceiling(n / (double)cols);
        var cellW = Math.Max(1, (size.Width - Gap * (cols + 1)) / cols);
        var cellH = Math.Max(1, (areaH - Gap * (rows + 1)) / rows);
        for (var i = 0; i < n; i++)
        {
            var row = i / cols;
            var col = i % cols;
            var inRow = row == rows - 1 ? n - row * cols : cols;
            var offset = (cols - inRow) * (cellW + Gap) / 2;          // dernière ligne centrée
            rects[i] = new Rect(Gap + offset + col * (cellW + Gap), areaTop + Gap + row * (cellH + Gap), cellW, cellH);
        }
        return rects;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        var rects = Compute(size);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(rects[i].Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rects = Compute(finalSize);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var z = GetMode(child) == CallTileMode.Pip ? 5 : 0;      // ma vignette passe au-dessus de la vidéo plein écran
            if (GetZIndex(child) != z) SetZIndex(child, z);
            child.Arrange(rects[i]);
        }
        return finalSize;
    }

}
