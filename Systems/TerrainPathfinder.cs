using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;

namespace SimuFarmer.Systems
{
    public static class TerrainPathfinder
    {
        // 8-directional movement: cardinal + diagonal
        private static readonly (int dx, int dy, float baseCost)[] Neighbors =
        {
            ( 1,  0, 1.0f),
            (-1,  0, 1.0f),
            ( 0,  1, 1.0f),
            ( 0, -1, 1.0f),
            ( 1,  1, 1.414f),
            ( 1, -1, 1.414f),
            (-1,  1, 1.414f),
            (-1, -1, 1.414f),
        };

        public static bool HasLineOfSight(GameController gc, Vector2 a, Vector2 b, int minWalkable = 3)
        {
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            if (pfGrid == null || pfGrid.Length == 0) return true; // Fallback if data unavailable

            int rows = pfGrid.Length;
            int cols = pfGrid[0].Length;

            var ax = (int)a.X;
            var ay = (int)a.Y;
            var bx = (int)b.X;
            var by = (int)b.Y;

            // Target out of grid bounds or inside solid impassable wall (0)
            if (bx < 0 || bx >= cols || by < 0 || by >= rows || pfGrid[by][bx] == 0)
                return false;

            var dx = Math.Abs(bx - ax);
            var dy = Math.Abs(by - ay);
            var sx = ax < bx ? 1 : -1;
            var sy = ay < by ? 1 : -1;
            var err = dx - dy;

            var cx = ax;
            var cy = ay;

            while (cx != bx || cy != by)
            {
                var e2 = 2 * err;
                if (e2 > -dy) { err -= dy; cx += sx; }
                if (e2 < dx) { err += dx; cy += sy; }

                if (cx < 0 || cx >= cols || cy < 0 || cy >= rows)
                    return false;

                // Solid impassable wall
                if (pfGrid[cy][cx] == 0)
                    return false;

                // Check intermediate cells for walkability (ignore final arrival cell if it's on a fringe)
                if (cx != bx || cy != by)
                {
                    if (pfGrid[cy][cx] < minWalkable)
                        return false;
                }
            }

            return true;
        }

        public static Vector2 GetNextStepTowards(GameController gc, Vector2 start, Vector2 goal, float stepDist = 16f)
        {
            var dir = goal - start;
            float dist = dir.Length();
            if (dist <= stepDist)
                return goal;

            // 1. If line-of-sight is completely clear of walls, take direct step
            if (HasLineOfSight(gc, start, goal, 3))
            {
                return start + Vector2.Normalize(dir) * stepDist;
            }

            // 2. Line of sight blocked by wall/stairs -> Run A* pathfinding
            var path = FindPath(gc, start, goal);
            if (path != null && path.Count > 1)
            {
                // Find farthest waypoint along the path that has direct line-of-sight from start (within stepDist + 6)
                for (int i = path.Count - 1; i >= 1; i--)
                {
                    var wp = path[i];
                    float d = Vector2.Distance(start, wp);
                    if (d <= stepDist + 6f && HasLineOfSight(gc, start, wp, 3))
                    {
                        return wp;
                    }
                }
                return path[1];
            }

            // Fallback: direct step
            return start + Vector2.Normalize(dir) * stepDist;
        }

        public static List<Vector2> FindPath(GameController gc, Vector2 gridStart, Vector2 gridEnd, int maxNodes = 8000)
        {
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            if (pfGrid == null || pfGrid.Length == 0)
                return new List<Vector2> { gridStart, gridEnd };

            var rows = pfGrid.Length;
            var cols = pfGrid[0].Length;

            var sx = (int)gridStart.X;
            var sy = (int)gridStart.Y;
            var gx = (int)gridEnd.X;
            var gy = (int)gridEnd.Y;

            sx = Math.Clamp(sx, 0, cols - 1);
            sy = Math.Clamp(sy, 0, rows - 1);
            gx = Math.Clamp(gx, 0, cols - 1);
            gy = Math.Clamp(gy, 0, rows - 1);

            if (pfGrid[sy][sx] < 3) (sx, sy) = FindNearestWalkable(pfGrid, sx, sy, rows, cols);
            if (pfGrid[gy][gx] < 3) (gx, gy) = FindNearestWalkable(pfGrid, gx, gy, rows, cols);

            if (pfGrid[sy][sx] < 3 || pfGrid[gy][gx] < 3)
                return new List<Vector2> { gridStart, gridEnd };

            var open = new PriorityQueue<(int x, int y), float>();
            var gScore = new Dictionary<(int, int), float>();
            var cameFrom = new Dictionary<(int, int), (int x, int y)>();

            gScore[(sx, sy)] = 0;
            open.Enqueue((sx, sy), Heuristic(sx, sy, gx, gy));

            int explored = 0;
            (int x, int y) bestNode = (sx, sy);
            float bestHeuristic = Heuristic(sx, sy, gx, gy);

            while (open.Count > 0 && explored < maxNodes)
            {
                var (cx, cy) = open.Dequeue();
                explored++;

                float h = Heuristic(cx, cy, gx, gy);
                if (h < bestHeuristic)
                {
                    bestHeuristic = h;
                    bestNode = (cx, cy);
                }

                if (cx == gx && cy == gy)
                {
                    var rawPath = ReconstructPath(cameFrom, sx, sy, gx, gy);
                    return SmoothPath(pfGrid, rawPath);
                }

                var currentG = gScore.GetValueOrDefault((cx, cy), float.MaxValue);
                if (currentG == float.MaxValue) continue;

                foreach (var (dx, dy, baseCost) in Neighbors)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;

                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows) continue;

                    int cellValue = pfGrid[ny][nx];
                    if (cellValue < 3) continue; // Walkable cells only

                    float moveCost = baseCost * (6 - cellValue);
                    float tentativeG = currentG + moveCost;
                    var key = (nx, ny);

                    if (!gScore.TryGetValue(key, out var existingG) || tentativeG < existingG)
                    {
                        gScore[key] = tentativeG;
                        cameFrom[key] = (cx, cy);
                        open.Enqueue(key, tentativeG + Heuristic(nx, ny, gx, gy));
                    }
                }
            }

            // If node budget was exhausted in a long winding corridor, return path to the closest explored node
            // rather than a blind straight line into a wall
            if (cameFrom.ContainsKey(bestNode) && bestNode != (sx, sy))
            {
                var partialPath = ReconstructPath(cameFrom, sx, sy, bestNode.x, bestNode.y);
                return SmoothPath(pfGrid, partialPath);
            }

            return new List<Vector2> { gridStart, gridEnd };
        }

        private static (int x, int y) FindNearestWalkable(int[][] pfGrid, int startX, int startY, int rows, int cols)
        {
            for (int r = 1; r <= 15; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                        int nx = startX + dx;
                        int ny = startY + dy;
                        if (nx >= 0 && nx < cols && ny >= 0 && ny < rows && pfGrid[ny][nx] >= 3)
                            return (nx, ny);
                    }
                }
            }
            return (startX, startY);
        }

        private static float Heuristic(int ax, int ay, int bx, int by)
        {
            var dx = Math.Abs(ax - bx);
            var dy = Math.Abs(ay - by);
            return Math.Max(dx, dy) + 0.414f * Math.Min(dx, dy);
        }

        private static List<Vector2> ReconstructPath(Dictionary<(int, int), (int x, int y)> cameFrom, int sx, int sy, int gx, int gy)
        {
            var path = new List<Vector2>();
            var current = (gx, gy);
            while (current != (sx, sy))
            {
                path.Add(new Vector2(current.Item1, current.Item2));
                current = cameFrom[current];
            }
            path.Add(new Vector2(sx, sy));
            path.Reverse();
            return path;
        }

        private static List<Vector2> SmoothPath(int[][] grid, List<Vector2> path)
        {
            if (path.Count <= 2) return path;

            int rows = grid.Length;
            int cols = grid[0].Length;
            var result = new List<Vector2> { path[0] };
            int current = 0;

            while (current < path.Count - 1)
            {
                int farthest = current + 1;
                for (int i = path.Count - 1; i > current + 1; i--)
                {
                    if (Vector2.Distance(path[current], path[i]) > 35f) continue;

                    if (HasLineOfSightGrid(grid, path[current], path[i], rows, cols, 3))
                    {
                        farthest = i;
                        break;
                    }
                }
                result.Add(path[farthest]);
                current = farthest;
            }

            return result;
        }

        private static bool HasLineOfSightGrid(int[][] grid, Vector2 a, Vector2 b, int rows, int cols, int minWalkable = 3)
        {
            var ax = (int)a.X;
            var ay = (int)a.Y;
            var bx = (int)b.X;
            var by = (int)b.Y;

            var dx = Math.Abs(bx - ax);
            var dy = Math.Abs(by - ay);
            var sx = ax < bx ? 1 : -1;
            var sy = ay < by ? 1 : -1;
            var err = dx - dy;

            var cx = ax;
            var cy = ay;

            while (cx != bx || cy != by)
            {
                if (cx < 0 || cx >= cols || cy < 0 || cy >= rows || grid[cy][cx] < minWalkable)
                    return false;

                var e2 = 2 * err;
                if (e2 > -dy) { err -= dy; cx += sx; }
                if (e2 < dx) { err += dx; cy += sy; }
            }

            return true;
        }
    }
}
