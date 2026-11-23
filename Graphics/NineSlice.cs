using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ZeEngine.Graphics;

/// <summary>
/// A texture cut into a 3x3 grid. The four corners are drawn at a fixed size, the four
/// edges stretch along one axis, and the center stretches both ways, so one small
/// texture can frame a panel of any size without distorting its borders.
///
/// Draw with SamplerState.PointClamp so pixel art stays crisp and edges don't bleed.
/// </summary>
public sealed class NineSlice
{
    private readonly Rectangle[] _source = new Rectangle[9]; // row-major: TL T TR / L C R / BL B BR

    public Texture2D Texture { get; }
    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }

    /// <param name="left">Width in texture pixels of the left border (and so on for the others).</param>
    public NineSlice(Texture2D texture, int left, int top, int right, int bottom)
    {
        Texture = texture ?? throw new ArgumentNullException(nameof(texture));

        if (left < 0 || top < 0 || right < 0 || bottom < 0 ||
            left + right >= texture.Width || top + bottom >= texture.Height)
        {
            throw new ArgumentException("Borders must be non-negative and leave a non-empty center.");
        }

        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;

        int centerWidth = texture.Width - left - right;
        int centerHeight = texture.Height - top - bottom;

        int[] xs = { 0, left, texture.Width - right };
        int[] ws = { left, centerWidth, right };
        int[] ys = { 0, top, texture.Height - bottom };
        int[] hs = { top, centerHeight, bottom };

        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                _source[row * 3 + col] = new Rectangle(xs[col], ys[row], ws[col], hs[row]);
    }

    /// <summary>
    /// Draws the slice into <paramref name="destination"/>.
    /// <paramref name="scale"/> multiplies the border thickness on screen (use your UI zoom so the
    /// panel matches the scaled pixel-art UI). If the destination is smaller than the borders,
    /// the borders shrink proportionally instead of overlapping.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle destination, Color color, int scale = 1)
    {
        if (destination.Width <= 0 || destination.Height <= 0) return;
        scale = Math.Max(1, scale);

        float left = Left * scale, right = Right * scale;
        float top = Top * scale, bottom = Bottom * scale;

        if (left + right > destination.Width)
        {
            float k = destination.Width / (left + right);
            left *= k; right *= k;
        }

        if (top + bottom > destination.Height)
        {
            float k = destination.Height / (top + bottom);
            top *= k; bottom *= k;
        }

        Span<int> xs = stackalloc int[4]
        {
            destination.X,
            destination.X + (int)MathF.Round(left),
            destination.Right - (int)MathF.Round(right),
            destination.Right
        };

        Span<int> ys = stackalloc int[4]
        {
            destination.Y,
            destination.Y + (int)MathF.Round(top),
            destination.Bottom - (int)MathF.Round(bottom),
            destination.Bottom
        };

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                var target = new Rectangle(xs[col], ys[row], xs[col + 1] - xs[col], ys[row + 1] - ys[row]);
                if (target.Width <= 0 || target.Height <= 0) continue;

                spriteBatch.Draw(Texture, target, _source[row * 3 + col], color);
            }
        }
    }

    /// <summary>
    /// Builds a small rounded-rectangle panel texture in code (outline, inner highlight, fill),
    /// so you have a working 9-slice without any art. Replace it with a real texture later:
    /// new NineSlice(Content.Load&lt;Texture2D&gt;("ui/panel"), 4, 4, 4, 4).
    /// </summary>
    public static NineSlice CreatePanel(GraphicsDevice graphicsDevice)
        => CreatePanel(graphicsDevice, new Color(28, 32, 48), new Color(10, 12, 20), new Color(70, 80, 110));

    public static NineSlice CreatePanel(GraphicsDevice graphicsDevice, Color fill, Color border, Color highlight)
    {
        const int size = 12;     // texture is 12x12, borders are 4px, so the center is a 4x4 stretch area
        const int margin = 4;
        const float radius = 3f; // corner radius in texture pixels

        var pixels = new Color[size * size];
        float half = size / 2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Signed distance from this pixel's center to the rounded rectangle's edge.
                float px = MathF.Abs(x + 0.5f - half) - (half - radius);
                float py = MathF.Abs(y + 0.5f - half) - (half - radius);
                float outside = MathF.Sqrt(MathF.Max(px, 0f) * MathF.Max(px, 0f) + MathF.Max(py, 0f) * MathF.Max(py, 0f));
                float distance = outside + MathF.Min(MathF.Max(px, py), 0f) - radius;

                if (distance > 0f)
                    pixels[y * size + x] = Color.Transparent;      // outside the rounded corner
                else if (-distance < 1f)
                    pixels[y * size + x] = border;                 // 1px outline
                else if (-distance < 2f)
                    pixels[y * size + x] = highlight;              // 1px inner highlight
                else
                    pixels[y * size + x] = fill;
            }
        }

        var texture = new Texture2D(graphicsDevice, size, size);
        texture.SetData(pixels);
        return new NineSlice(texture, margin, margin, margin, margin);
    }
}
