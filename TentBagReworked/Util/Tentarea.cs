using System;
using System.Collections.Generic;
using TentBagReworked.Config;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace TentBagReworked.Util;

/// <summary>
/// Общие геометрические расчёты зоны палатки. Вынесено отдельно, чтобы серверная
/// логика сбора (PackableBehavior.PackContents) и клиентская подсветка зоны
/// (горячая клавиша в TentBagReworkedModSystem) использовали ОДИН и тот же расчёт
/// и не могли разойтись.
/// </summary>
public static class TentArea
{
    /// <summary>
    /// Считает кубоид зоны сбора (включительно с обоими углами) для ПУСТОГО мешка,
    /// исходя из блока, на который смотрит игрок.
    /// Должно совпадать с расчётом в PackableBehavior.PackContents.
    /// </summary>
    public static void GetPackBounds(TentBagReworkedConfig config, IBlockAccessor blockAccessor, BlockPos lookedAt, out BlockPos start, out BlockPos end)
    {
        // Тот же критерий, что и PackableBehavior.IsPlantOrRock
        Block? block = blockAccessor.GetBlock(lookedAt);
        bool plantOrRock = config.ReplacePlantsAndRocks && block?.Replaceable is >= 5500 and <= 6500;

        int y = plantOrRock ? 1 : 0;
        int floorShift = config.GrabFloor ? -1 : 0;

        start = lookedAt.AddCopy(-config.MaxRadius, 1 - y + floorShift, -config.MaxRadius);
        end = lookedAt.AddCopy(config.MaxRadius, Math.Max(config.MaxHeight, 3), config.MaxRadius);
    }

    /// <summary>
    /// Возвращает позиции блоков, лежащих на 12 рёбрах кубоида .
    /// Блок попадает в каркас, если минимум две из трёх его координат лежат на границе.
    /// Так подсветка показывает габариты зоны, не заполняя её целиком и не перекрывая обзор.
    /// </summary>
    public static List<BlockPos> GetCuboidEdges(BlockPos a, BlockPos b)
    {
        int minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
        int minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
        int minZ = Math.Min(a.Z, b.Z), maxZ = Math.Max(a.Z, b.Z);
        int dim = a.dimension;

        List<BlockPos> edges = new();
        for (int x = minX; x <= maxX; x++)
        {
            bool xEdge = x == minX || x == maxX;
            for (int y = minY; y <= maxY; y++)
            {
                bool yEdge = y == minY || y == maxY;
                for (int z = minZ; z <= maxZ; z++)
                {
                    bool zEdge = z == minZ || z == maxZ;
                    int onBoundary = (xEdge ? 1 : 0) + (yEdge ? 1 : 0) + (zEdge ? 1 : 0);
                    if (onBoundary >= 2)
                    {
                        edges.Add(new BlockPos(x, y, z, dim));
                    }
                }
            }
        }

        return edges;
    }
}