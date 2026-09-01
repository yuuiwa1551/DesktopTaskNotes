namespace DesktopTaskNotes.Services;

public readonly record struct SnapRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);

    public SnapRectangle MoveTo(int left, int top) =>
        new(left, top, left + Width, top + Height);

    public bool Intersects(SnapRectangle other) =>
        Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}

public static class WindowSnapService
{
    public const int DefaultSnapDistanceDip = 8;

    public static SnapRectangle Snap(
        SnapRectangle moving,
        SnapRectangle workArea,
        IReadOnlyList<SnapRectangle> obstacles,
        int snapDistance,
        bool constrainToWorkArea = true)
    {
        if (moving.Width <= 0 || moving.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0)
            return moving;

        var threshold = Math.Max(0, snapDistance);
        var relevantObstacles = obstacles
            .Where(rectangle => rectangle.Width > 0 && rectangle.Height > 0 &&
                                (!constrainToWorkArea || TouchesWorkArea(rectangle, workArea)))
            .ToArray();
        var result = constrainToWorkArea ? ClampInside(moving, workArea) : moving;

        var snappedLeft = result.Left;
        var snappedTop = result.Top;
        var bestHorizontalDistance = threshold + 1;
        var bestVerticalDistance = threshold + 1;

        ConsiderSnap(ref snappedLeft, ref bestHorizontalDistance, result.Left, workArea.Left, threshold);
        ConsiderSnap(ref snappedLeft, ref bestHorizontalDistance, result.Left,
            workArea.Right - result.Width, threshold);
        ConsiderSnap(ref snappedTop, ref bestVerticalDistance, result.Top, workArea.Top, threshold);
        ConsiderSnap(ref snappedTop, ref bestVerticalDistance, result.Top,
            workArea.Bottom - result.Height, threshold);

        foreach (var obstacle in relevantObstacles)
        {
            if (OverlapLength(result.Top, result.Bottom, obstacle.Top, obstacle.Bottom) > 0)
            {
                ConsiderSnap(ref snappedLeft, ref bestHorizontalDistance, result.Left,
                    obstacle.Left - result.Width, threshold);
                ConsiderSnap(ref snappedLeft, ref bestHorizontalDistance, result.Left,
                    obstacle.Right, threshold);
            }

            if (OverlapLength(result.Left, result.Right, obstacle.Left, obstacle.Right) > 0)
            {
                ConsiderSnap(ref snappedTop, ref bestVerticalDistance, result.Top,
                    obstacle.Top - result.Height, threshold);
                ConsiderSnap(ref snappedTop, ref bestVerticalDistance, result.Top,
                    obstacle.Bottom, threshold);
            }
        }

        result = result.MoveTo(snappedLeft, snappedTop);
        return constrainToWorkArea ? ClampInside(result, workArea) : result;
    }

    private static void ConsiderSnap(
        ref int currentValue,
        ref int bestDistance,
        int originalValue,
        int candidateValue,
        int threshold)
    {
        var distance = Math.Abs(candidateValue - originalValue);
        if (distance > threshold || distance >= bestDistance) return;
        currentValue = candidateValue;
        bestDistance = distance;
    }

    private static SnapRectangle ClampInside(SnapRectangle rectangle, SnapRectangle workArea)
    {
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - rectangle.Width);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - rectangle.Height);
        var left = Math.Clamp(rectangle.Left, workArea.Left, maximumLeft);
        var top = Math.Clamp(rectangle.Top, workArea.Top, maximumTop);
        return rectangle.MoveTo(left, top);
    }

    private static bool TouchesWorkArea(SnapRectangle rectangle, SnapRectangle workArea) =>
        rectangle.Left <= workArea.Right && rectangle.Right >= workArea.Left &&
        rectangle.Top <= workArea.Bottom && rectangle.Bottom >= workArea.Top;

    private static int OverlapLength(int firstStart, int firstEnd, int secondStart, int secondEnd) =>
        Math.Max(0, Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart));
}
