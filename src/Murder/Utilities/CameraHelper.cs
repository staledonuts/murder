﻿using Murder.Core;
using Murder.Core.Graphics;

namespace Murder.Utilities
{
    public static class CameraHelper
    {
        public static (int minX, int maxX, int minY, int maxY) GetSafeGridBounds(this Camera2D camera, int width, int height)
        {
            int minX = Math.Max(0, Calculator.FloorToInt(camera.Bounds.Left / Grid.CellSize) - 2);
            int maxX = Math.Min(width + 1, Calculator.CeilToInt(camera.Bounds.Right / Grid.CellSize) + 2);

            int minY = Math.Max(0, Calculator.FloorToInt(camera.Bounds.Top / Grid.CellSize) - 2);
            int maxY = Math.Min(height + 1, Calculator.CeilToInt(camera.Bounds.Bottom / Grid.CellSize) + 2);

            return (minX, maxX, minY, maxY);
        }
    }
}