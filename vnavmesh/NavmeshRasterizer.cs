﻿using DotRecast.Core;
using DotRecast.Recast;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

// utility to rasterize various meshes into a heightfield
public class NavmeshRasterizer
{
    // cheap triangle-bbox test: if all 3 vertices are on the same side of the bbox plane, the triangle can be discarded
    [Flags]
    private enum OutFlags : byte
    {
        None = 0,
        NegX = 1 << 0,
        PosX = 1 << 1,
        NegY = 1 << 2,
        PosY = 1 << 3,
        NegZ = 1 << 4,
        PosZ = 1 << 5,
    }

    // a set of per-cell intersections with vertical ray
    // high 31 bits of entry is the remapped Y coordinate (0 = -1024, 0x7fffffff = 1024-eps), low bit is normal sign (1 if points up)
    private class IntersectionSet
    {
        public const int PageShift = 20;
        public const int PageSize = 1 << PageShift;
        public const int ValueScale = 1 << 20; // 2048 = 2^11, we have 32 bits => 20 bits of scale; note: this is a bit extreme, mantissa is only 24 bits...
        public const float InvValueScale = 1.0f / ValueScale;

        public record struct Entry(int Next, uint Value);

        private int _numCellsX;
        private int[] _firstIndices; // x then z
        private List<Entry[]> _pages = new();
        private int _firstFree = 1;

        public static uint Encode(float value, bool normalUp) => ((uint)((value + 1024) * ValueScale) << 1) | (normalUp ? 1u : 0);
        public static float DecodeValue(uint v) => (v >> 1) * InvValueScale - 1024;
        public static bool DecodeNormalUp(uint v) => (v & 1) != 0;

        public IntersectionSet(int numCellsX, int numCellsZ)
        {
            _numCellsX = numCellsX;
            _firstIndices = new int[numCellsX * numCellsZ];
            _pages.Add(new Entry[PageSize]);
        }

        public void Add(int x, int z, float value, bool normalUp)
        {
            if (value <= -1024 || value >= 1024)
                return;
            var pageIndex = _firstFree >> PageShift;
            if (pageIndex == _pages.Count)
                _pages.Add(new Entry[PageSize]);
            var indexInPage = _firstFree & (PageSize - 1);
            var ival = Encode(value, normalUp);
            ref var first = ref _firstIndices[z * _numCellsX + x];
            _pages[pageIndex][indexInPage] = new(first, ival);
            first = _firstFree++;
        }

        public int FetchSorted(int x, int z, Span<uint> buffer)
        {
            var idx = _firstIndices[z * _numCellsX + x];
            if (idx == 0)
                return 0;

            int cnt = 0;
            do
            {
                var entry = _pages[idx >> PageShift][idx & (PageSize - 1)];
                buffer[cnt++] = entry.Value;
                idx = entry.Next;
            }
            while (idx != 0);
            buffer.Slice(0, cnt).Sort();
            return cnt;
        }

        public void Clear()
        {
            Array.Fill(_firstIndices, 0);
            _firstFree = 1;
        }
    }

    private RcHeightfield _heightfield;
    private RcContext _telemetry;
    private int _walkableClimbThreshold; // if two spans have maximums within this number of voxels, their area is 'merged' (higher is selected)
    private float _walkableNormalThreshold; // triangle is considered 'walkable' if it's world-space normal's Y coordinate is >= this

    public NavmeshRasterizer(RcHeightfield heightfield, float walkableNormalThreshold, int walkableMaxClimb, RcContext telemetry)
    {
        _heightfield = heightfield;
        _telemetry = telemetry;
        _walkableClimbThreshold = walkableMaxClimb;
        _walkableNormalThreshold = walkableNormalThreshold;
    }

    public void Rasterize(SceneExtractor geom, bool includeTerrain, bool includeMeshes, bool includeAnalytic, bool fillInteriors)
    {
        Span<Vector3> worldVertices = stackalloc Vector3[256];
        Span<OutFlags> outFlags = stackalloc OutFlags[256];
        Span<Vector3> clipRemainingZ = stackalloc Vector3[7];
        Span<Vector3> clipRemainingX = stackalloc Vector3[7];
        Span<Vector3> clipScratch = stackalloc Vector3[7];
        Span<Vector3> clipCell = stackalloc Vector3[7];
        Span<uint> interiors = stackalloc uint[256];
        float invCellXZ = 1.0f / _heightfield.cs;
        float invCellY = 1.0f / _heightfield.ch;
        float worldHeight = _heightfield.bmax.Y - _heightfield.bmin.Y;
        var iset = fillInteriors ? new IntersectionSet(_heightfield.width, _heightfield.height) : null;
        foreach (var (name, mesh) in geom.Meshes)
        {
            var terrain = mesh.MeshFlags.HasFlag(SceneExtractor.MeshFlags.FromTerrain);
            var analytic = mesh.MeshFlags.HasFlag(SceneExtractor.MeshFlags.FromAnalyticShape);
            bool include = terrain ? includeTerrain : analytic ? includeAnalytic : includeMeshes;
            if (!include)
                continue;

            foreach (var instance in mesh.Instances)
            {
                if (instance.WorldBounds.Max.X <= _heightfield.bmin.X || instance.WorldBounds.Max.Z <= _heightfield.bmin.Z || instance.WorldBounds.Min.X >= _heightfield.bmax.X || instance.WorldBounds.Min.Z >= _heightfield.bmax.Z)
                    continue;

                foreach (var part in mesh.Parts)
                {
                    // fill vertex buffer
                    int iv = 0;
                    foreach (var v in part.Vertices)
                    {
                        var w = instance.WorldTransform.TransformCoordinate(v);
                        var f = OutFlags.None;
                        if (w.X <= _heightfield.bmin.X) f |= OutFlags.NegX;
                        if (w.X >= _heightfield.bmax.X) f |= OutFlags.PosX;
                        if (w.Y <= _heightfield.bmin.Y) f |= OutFlags.NegY;
                        if (w.Y >= _heightfield.bmax.Y) f |= OutFlags.PosY;
                        if (w.Z <= _heightfield.bmin.Z) f |= OutFlags.NegZ;
                        if (w.Z >= _heightfield.bmax.Z) f |= OutFlags.PosZ;
                        worldVertices[iv] = w;
                        outFlags[iv] = f;
                        ++iv;
                    }

                    // TODO: move area-id calculations to extraction step + store indices in a form that allows using RasterizeTriangles()
                    foreach (var p in part.Primitives)
                    {
                        var flags = (p.Flags & ~instance.ForceClearPrimFlags) | instance.ForceSetPrimFlags;
                        if (flags.HasFlag(SceneExtractor.PrimitiveFlags.FlyThrough))
                            continue; // TODO: rasterize to normal heightfield, can't do it right now, since we're using same heightfield for both mesh and volume

                        if ((outFlags[p.V1] & outFlags[p.V2] & outFlags[p.V3]) != OutFlags.None)
                            continue; // vertex is fully outside bounds, on one side of some plane

                        var v1 = worldVertices[p.V1];
                        var v2 = worldVertices[p.V2];
                        var v3 = worldVertices[p.V3];
                        var v12 = v2 - v1;
                        var v13 = v3 - v1;
                        var normal = Vector3.Normalize(Vector3.Cross(v12, v13));

                        bool unwalkable = flags.HasFlag(SceneExtractor.PrimitiveFlags.ForceUnwalkable) || normal.Y < _walkableNormalThreshold;
                        bool unlandable = flags.HasFlag(SceneExtractor.PrimitiveFlags.Unlandable);
                        var areaId = unwalkable ? 0 : unlandable ? Navmesh.UnlandableAreaId : RcConstants.RC_WALKABLE_AREA;

                        // prepare for clipping: while iterating over z, we'll keep the 'remaining polygon' in clipRemainingZ
                        int numRemainingZ = 0;
                        clipRemainingZ[numRemainingZ++] = v1;
                        clipRemainingZ[numRemainingZ++] = v2;
                        clipRemainingZ[numRemainingZ++] = v3;

                        // calculate the footprint of the triangle on the grid's z-axis
                        var (minZ, maxZ) = AxisMinMax(clipRemainingZ, numRemainingZ, 2);
                        int z0 = (int)((minZ - _heightfield.bmin.Z) * invCellXZ); // TODO: not sure whether this is correct (round to 0 instead of floor...)
                        int z1 = (int)((maxZ - _heightfield.bmin.Z) * invCellXZ);
                        // note: no need to check for fully outside here, it was checked before
                        z0 = Math.Clamp(z0, -1, _heightfield.height - 1); // use -1 rather than 0 to cut the polygon properly at the start of the tile
                        z1 = Math.Clamp(z1, 0, _heightfield.height - 1);

                        for (int z = z0; z <= z1; ++z)
                        {
                            if (numRemainingZ < 3)
                                break;

                            // clip polygon to 'row'
                            var cellZMax = _heightfield.bmin.Z + (z + 1) * _heightfield.cs;
                            int numRemainingX = SplitConvexPoly(clipRemainingZ, clipRemainingX, clipScratch, ref numRemainingZ, 2, cellZMax);

                            // previous buffer is now new scratch
                            var swapZ = clipRemainingZ;
                            clipRemainingZ = clipScratch;
                            clipScratch = swapZ;

                            if (numRemainingX < 3 || z < 0)
                                continue;

                            // find x bounds of the row
                            var (minX, maxX) = AxisMinMax(clipRemainingX, numRemainingX, 0);
                            int x0 = (int)((minX - _heightfield.bmin.X) * invCellXZ); // TODO: not sure whether this is correct (round to 0 instead of floor...)
                            int x1 = (int)((maxX - _heightfield.bmin.X) * invCellXZ);
                            if (x1 < 0 || x0 >= _heightfield.width)
                                continue;
                            x0 = Math.Clamp(x0, -1, _heightfield.width - 1);
                            x1 = Math.Clamp(x1, 0, _heightfield.width - 1);

                            var cellZMid = _heightfield.bmin.Z + (z + 0.5f) * _heightfield.cs;
                            for (int x = x0; x <= x1; ++x)
                            {
                                if (numRemainingX < 3)
                                    break;

                                // clip polygon to 'column'
                                var cellXMax = _heightfield.bmin.X + (x + 1) * _heightfield.cs;
                                int numCell = SplitConvexPoly(clipRemainingX, clipCell, clipScratch, ref numRemainingX, 0, cellXMax);

                                // previous buffer is now new scratch
                                var swapX = clipRemainingX;
                                clipRemainingX = clipScratch;
                                clipScratch = swapX;

                                if (numCell < 3 || x < 0)
                                    continue;

                                // find y bounds of the cell (TODO: this can probably be slightly simplified)
                                var (minY, maxY) = AxisMinMax(clipCell, numCell, 1);
                                minY -= _heightfield.bmin.Y;
                                maxY -= _heightfield.bmin.Y;
                                if (maxY < 0 || minY > worldHeight)
                                    continue;
                                minY = Math.Max(minY, 0);
                                maxY = Math.Min(maxY, worldHeight);
                                int y0 = Math.Clamp((int)MathF.Floor(minY * invCellY), 0, RcConstants.RC_SPAN_MAX_HEIGHT);
                                int y1 = Math.Clamp((int)MathF.Ceiling(maxY * invCellY), y0 + 1, RcConstants.RC_SPAN_MAX_HEIGHT);

                                RcRasterizations.AddSpan(_heightfield, x, z, y0, y1, areaId, _walkableClimbThreshold);

                                if (iset != null)
                                {
                                    // intersect a ray passing through the middle of the cell vertically with the triangle
                                    // A + AB*b + AC*c = P, b >= 0, c >= 0, a + b <= 1
                                    //  ==>
                                    // ABx*b + ACx*c = APx
                                    // ABz*b + ACz*c = APz
                                    //  ==> ABx*b = APx - ACx*c, ABz*ABx*b + ACz*ABx*c = APz*ABx
                                    //  ==> ABz*(APx - ACx*c) + ACz*ABx*c = APz*ABx
                                    //  ==> c = (APz*ABx - APx*ABz) / (ACz*ABx - ACx*ABz)
                                    //  ==> b = (APx*ACz*ABx - APx*ACx*ABz - ACx*APz*ABx + ACx*APx*ABz) / ABx*(ACz*ABx - ACx*ABz)
                                    //  ==> b = (APx*ACz - APz*ACx) / (ACz*ABx - ACx*ABz)
                                    //  ==> y = Ay + ABy*b + ACy*c
                                    var cellXMid = _heightfield.bmin.X + (x + 0.5f) * _heightfield.cs;
                                    var div = v13.Z * v12.X - v13.X * v12.Z;
                                    if (div != 0)
                                    {
                                        var apx = cellXMid - v1.X;
                                        var apz = cellZMid - v1.Z;
                                        var invDiv = 1.0f / div;
                                        var c = (apz * v12.X - apx * v12.Z) * invDiv;
                                        var b = (apx * v13.Z - apz * v13.X) * invDiv;
                                        if (c >= 0 && b >= 0 && c + b <= 1)
                                        {
                                            var intersectY = v1.Y + b * v12.Y + c * v13.Y;
                                            iset.Add(x, z, intersectY, normal.Y > 0);
                                        }
                                        // else: intersection is outside triangle
                                    }
                                    // else: normal is orthogonal to OY
                                }
                            }
                        }
                    }
                }

                if (iset != null)
                {
                    // fill interiors
                    int iz0 = Math.Clamp((int)((instance.WorldBounds.Min.Z - _heightfield.bmin.Z) * invCellXZ), 0, _heightfield.height - 1);
                    int iz1 = Math.Clamp((int)((instance.WorldBounds.Max.Z - _heightfield.bmin.Z) * invCellXZ), 0, _heightfield.height - 1);
                    int ix0 = Math.Clamp((int)((instance.WorldBounds.Min.X - _heightfield.bmin.X) * invCellXZ), 0, _heightfield.width - 1);
                    int ix1 = Math.Clamp((int)((instance.WorldBounds.Max.X - _heightfield.bmin.X) * invCellXZ), 0, _heightfield.width - 1);
                    for (int z = iz0; z <= iz1; ++z)
                    {
                        for (int x = ix0; x <= ix1; ++x)
                        {
                            var cnt = iset.FetchSorted(x, z, interiors);
                            if (cnt == 0)
                                continue; // empty

                            int idx = 0;
                            if (IntersectionSet.DecodeNormalUp(interiors[idx]))
                            {
                                // non-manifold mesh, assume everything below is interior
                                var maxY = IntersectionSet.DecodeValue(interiors[idx]) - _heightfield.bmin.Y;
                                int y1 = Math.Clamp((int)MathF.Ceiling(maxY * invCellY), 0, RcConstants.RC_SPAN_MAX_HEIGHT) - 1;
                                if (y1 > 0)
                                    RcRasterizations.AddSpan(_heightfield, x, z, 0, y1, 0, _walkableClimbThreshold);
                                ++idx;
                            }

                            while (true)
                            {
                                while (idx < cnt && IntersectionSet.DecodeNormalUp(interiors[idx]))
                                    ++idx;
                                if (idx == cnt)
                                    break;
                                var minY = IntersectionSet.DecodeValue(interiors[idx]) - _heightfield.bmin.Y;
                                while (idx < cnt && !IntersectionSet.DecodeNormalUp(interiors[idx]))
                                    ++idx;
                                if (idx == cnt)
                                    break;
                                var maxY = IntersectionSet.DecodeValue(interiors[idx]) - _heightfield.bmin.Y;
                                int y0 = Math.Clamp((int)MathF.Ceiling(minY * invCellY), 0, RcConstants.RC_SPAN_MAX_HEIGHT) + 1;
                                int y1 = Math.Clamp((int)MathF.Ceiling(maxY * invCellY), 0, RcConstants.RC_SPAN_MAX_HEIGHT) - 1;
                                if (y1 > y0)
                                    RcRasterizations.AddSpan(_heightfield, x, z, y0, y1, 0, _walkableClimbThreshold);
                            }
                        }
                    }
                    iset.Clear();
                }
            }
        }
    }

    // TODO: remove after i'm confident in my replacement code
    public void RasterizeOld(SceneExtractor geom, bool includeTerrain, bool includeMeshes, bool includeAnalytic)
    {
        float[] vertices = new float[3 * 256];
        foreach (var (name, mesh) in geom.Meshes)
        {
            var terrain = mesh.MeshFlags.HasFlag(SceneExtractor.MeshFlags.FromTerrain);
            var analytic = mesh.MeshFlags.HasFlag(SceneExtractor.MeshFlags.FromAnalyticShape);
            bool include = terrain ? includeTerrain : analytic ? includeAnalytic : includeMeshes;
            if (!include)
                continue;

            foreach (var inst in mesh.Instances)
            {
                if (inst.WorldBounds.Max.X <= _heightfield.bmin.X || inst.WorldBounds.Max.Z <= _heightfield.bmin.Z || inst.WorldBounds.Min.X >= _heightfield.bmax.X || inst.WorldBounds.Min.Z >= _heightfield.bmax.Z)
                    continue;

                foreach (var part in mesh.Parts)
                {
                    // fill vertex buffer
                    int iv = 0;
                    foreach (var v in part.Vertices)
                    {
                        var w = inst.WorldTransform.TransformCoordinate(v);
                        vertices[iv++] = w.X;
                        vertices[iv++] = w.Y;
                        vertices[iv++] = w.Z;
                    }

                    // TODO: move area-id calculations to extraction step + store indices in a form that allows using RasterizeTriangles()
                    foreach (var p in part.Primitives)
                    {
                        var flags = (p.Flags & ~inst.ForceClearPrimFlags) | inst.ForceSetPrimFlags;
                        if (flags.HasFlag(SceneExtractor.PrimitiveFlags.FlyThrough))
                            continue; // TODO: rasterize to normal heightfield, can't do it right now, since we're using same heightfield for both mesh and volume

                        bool walkable = !flags.HasFlag(SceneExtractor.PrimitiveFlags.ForceUnwalkable);
                        if (walkable)
                        {
                            var v1 = CachedVertex(vertices, p.V1);
                            var v2 = CachedVertex(vertices, p.V2);
                            var v3 = CachedVertex(vertices, p.V3);
                            var v12 = v2 - v1;
                            var v13 = v3 - v1;
                            var normal = Vector3.Normalize(Vector3.Cross(v12, v13));
                            walkable = normal.Y >= _walkableNormalThreshold;
                        }

                        var areaId = !walkable ? 0 : flags.HasFlag(SceneExtractor.PrimitiveFlags.Unlandable) ? Navmesh.UnlandableAreaId : RcConstants.RC_WALKABLE_AREA;
                        RcRasterizations.RasterizeTriangle(_telemetry, vertices, p.V1, p.V2, p.V3, areaId, _heightfield, _walkableClimbThreshold);
                    }
                }
            }
        }
    }

    private static Vector3 CachedVertex(ReadOnlySpan<float> vertices, int i)
    {
        var offset = 3 * i;
        return new(vertices[offset], vertices[offset + 1], vertices[offset + 2]);
    }

    private static (float min, float max) AxisMinMax(Span<Vector3> vertices, int count, int axis)
    {
        float min = vertices[0][axis];
        float max = min;
        for (int i = 1; i < count; ++i)
        {
            float v = vertices[i][axis];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        return (min, max);
    }

    // split a convex polygon along one of the axes
    // polygon 'smaller' than axis offset is to be processed next, rest is considered to be a leftover to process on the next iteration
    // count on input contains num vertices in src buffer, on output num vertices in remaining buffer
    private static int SplitConvexPoly(Span<Vector3> src, Span<Vector3> dest, Span<Vector3> remaining, ref int count, int axis, float axisOffset)
    {
        Span<float> axisDelta = stackalloc float[count];
        for (int i = 0; i < count; ++i)
            axisDelta[i] = axisOffset - src[i][axis];

        int cDest = 0, cRem = 0;
        var dPrev = axisDelta[count - 1];
        var vPrev = src[count - 1];
        for (int i = 0; i < count; ++i)
        {
            var dCurr = axisDelta[i];
            var vCurr = src[i];
            if ((dCurr >= 0) != (dPrev >= 0))
            {
                // two vertices are on the different sides of the separating axis
                float s = dPrev / (dPrev - dCurr);
                dest[cDest++] = remaining[cRem++] = vPrev + (vCurr - vPrev) * s;

                // add the i'th point to the right polygon; do NOT add points that are on the dividing line since these were already added above
                if (dCurr > 0)
                    dest[cDest++] = vCurr;
                else if (dCurr < 0)
                    remaining[cRem++] = vCurr;
            }
            else
            {
                // add the i'th point to the right polygon; addition is done even for points on the dividing line
                if (dCurr > 0)
                {
                    dest[cDest++] = vCurr;
                }
                else if (dCurr < 0)
                {
                    remaining[cRem++] = vCurr;
                }
                else
                {
                    dest[cDest++] = vCurr;
                    remaining[cRem++] = vCurr;
                }
            }
            dPrev = dCurr;
            vPrev = vCurr;
        }
        count = cRem;
        return cDest;
    }
}
