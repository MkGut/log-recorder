using System.Runtime.InteropServices;
using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

public sealed class ShapeTests
{
    [DllImport("gdi32.dll")] static extern int PtInRegion(nint rgn, int x, int y);
    [DllImport("gdi32.dll")] static extern int DeleteObject(nint o);

    // The "log" badge overhangs the casing's big top-left curve; the window region must not clip it.
    [Theory]
    [InlineData(510, 1.0)]
    [InlineData(395, 1.0)]
    [InlineData(510, 1.5)]
    [InlineData(320, 2.0)]
    public void RegionKeepsTheWholeBadge(int widthDip, double scale)
    {
        var rgn = SurfaceWindow.CreateShapeRegion((int)(widthDip * scale), (int)(300 * scale), scale, widthDip);
        try
        {
            var lens = Skin.LensRect();
            // Every point of the badge outline, pushed 1 px outward (its rim), must stay inside the window.
            double cx = (lens.L + lens.W / 2) * scale, cy = (lens.T + lens.H / 2) * scale;
            var outline = SurfaceWindow.Outline((int)Math.Round(lens.W * scale), (int)Math.Round(lens.H * scale), scale, Skin.LensShape,
                (int)Math.Round(lens.L * scale), (int)Math.Round(lens.T * scale));
            foreach (var p in outline)
            {
                double dx = p.x - cx, dy = p.y - cy, len = Math.Sqrt(dx * dx + dy * dy);
                var x = (int)Math.Round(p.x + dx / len);
                var y = (int)Math.Round(p.y + dy / len);
                Assert.True(PtInRegion(rgn, x, y) != 0, $"badge rim at {x},{y} is clipped");
            }

            // The window's far corner is still cut away, so the casing shape is kept.
            Assert.True(PtInRegion(rgn, 1, 1) == 0, "window corner");
        }
        finally
        {
            _ = DeleteObject(rgn);
        }
    }
}
