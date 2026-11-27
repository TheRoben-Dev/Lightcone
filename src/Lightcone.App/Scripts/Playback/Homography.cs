using Godot;

namespace Lightcone.App.Playback;

/// <summary>The perspective mapping that takes four points onto four others.</summary>
public static class Homography
{
    /// <summary>
    /// The 3×3 matrix (as three rows) that maps each <paramref name="from"/> corner onto the
    /// matching <paramref name="to"/> corner: (u, v, w) = M · (x, y, 1), then divide by w.
    /// </summary>
    public static Vector3[] Solve(IReadOnlyList<Vector2> from, IReadOnlyList<Vector2> to)
    {
        // Eight unknowns (h33 = 1), two equations per corner; Gaussian elimination with pivoting.
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            var r = i * 2;
            double[] eu = [x, y, 1, 0, 0, 0, -x * u, -y * u, u];
            double[] ev = [0, 0, 0, x, y, 1, -x * v, -y * v, v];
            for (var c = 0; c < 9; c++)
            {
                a[r, c] = eu[c];
                a[r + 1, c] = ev[c];
            }
        }
        for (var col = 0; col < 8; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 8; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }
            if (Math.Abs(a[pivot, col]) < 1e-12)
            {
                return [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];  // degenerate: identity
            }
            for (var c = 0; c < 9; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            }
            for (var r = 0; r < 8; r++)
            {
                if (r == col)
                {
                    continue;
                }
                var f = a[r, col] / a[col, col];
                for (var c = col; c < 9; c++)
                {
                    a[r, c] -= f * a[col, c];
                }
            }
        }
        var h = new double[9];
        for (var i = 0; i < 8; i++)
        {
            h[i] = a[i, 8] / a[i, i];
        }
        h[8] = 1;
        return [new((float)h[0], (float)h[1], (float)h[2]), new((float)h[3], (float)h[4], (float)h[5]), new((float)h[6], (float)h[7], (float)h[8])];
    }
}
